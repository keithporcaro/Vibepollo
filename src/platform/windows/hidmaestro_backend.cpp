/**
 * @file src/platform/windows/hidmaestro_backend.cpp
 * @brief HIDMaestro-backed gamepad backend.
 *
 * Host-side counterpart of the C# sidecar at `tools/hidmaestro_host/`. The
 * sidecar wraps HIDMaestro.Core; this class spawns it, exchanges a HELLO
 * handshake, forwards lifecycle / state frames, and dispatches incoming
 * RUMBLE / LED / TRIGGER_EFFECT frames back onto each pad's feedback queue.
 *
 * Plumbing reuses the existing IPC primitives in
 * `src/platform/windows/ipc/`: ProcessHandler (job-killed child), WinPipe
 * (client side of the sidecar's named-pipe server), FramedPipe (length-
 * prefixed framing), AsyncNamedPipe (dedicated read thread).
 */

#include "hidmaestro_backend.h"

#include <cstdint>
#include <filesystem>
#include <mutex>
#include <random>
#include <string>
#include <unordered_map>

#include "ipc/hidmaestro_ipc.h"
#include "ipc/pipes.h"
#include "ipc/process_handler.h"
#include "misc.h"
#include "src/config.h"
#include "src/logging.h"

namespace platf {

  using namespace std::literals;

  namespace {

    constexpr int kHelloTimeoutMs = 5000;
    constexpr int kSendTimeoutMs = 1000;

    std::wstring sidecar_path() {
      WCHAR exe_path[MAX_PATH] = {0};
      DWORD n = GetModuleFileNameW(nullptr, exe_path, _countof(exe_path));
      if (n == 0 || n >= _countof(exe_path)) {
        return {};
      }
      return (std::filesystem::path {exe_path}.parent_path() / L"tools" / L"vibepollo_hidmaestro_host.exe").wstring();
    }

    std::string make_pipe_name() {
      std::random_device rd;
      std::uniform_int_distribution<std::uint64_t> dist;
      return "vibepollo_hidmaestro_" + std::to_string(dist(rd));
    }

    // Map config + client metadata to a HIDMaestro profile id. Mirrors the
    // X360-vs-DS4 selection logic in vigem_t::alloc and adds two Valve-VID
    // outputs the sidecar can produce:
    //   * "steam-deck"            — bundled HIDMaestro profile (PID 0x1205,
    //                                full HID gamepad shape with paddles + IMU)
    //   * "steam-controller-2026" — transitional profile synthesized by the
    //                                sidecar from steam-deck with the published
    //                                SC2026 identity (VID 0x28DE / PID 0x1302).
    //                                Trackpads, grip sensors, and linear-actuator
    //                                haptics are not yet exposed by that
    //                                descriptor; sticks / triggers / dpad / 4
    //                                paddles / IMU work today.
    std::string select_profile(const gamepad_arrival_t &metadata) {
      if (config::input.gamepad == "x360"sv) {
        return "xbox-360-wired";
      }
      if (config::input.gamepad == "ds4"sv) {
        return "dualshock-4-v1-full";
      }
      if (config::input.gamepad == "steam-deck"sv) {
        return "steam-deck";
      }
      if (config::input.gamepad == "steam-controller-2026"sv) {
        return "steam-controller-2026";
      }
      if (metadata.type == LI_CTYPE_PS) {
        return "dualshock-4-v1-full";
      }
      if (metadata.type == LI_CTYPE_XBOX) {
        return "xbox-360-wired";
      }
      if (config::input.motion_as_ds4 && (metadata.capabilities & (LI_CCAP_ACCEL | LI_CCAP_GYRO))) {
        return "dualshock-4-v1-full";
      }
      if (config::input.touchpad_as_ds4 && (metadata.capabilities & LI_CCAP_TOUCHPAD)) {
        return "dualshock-4-v1-full";
      }
      return "xbox-360-wired";
    }

  }  // namespace

  struct hidmaestro_t::impl_t {
    ProcessHandler proc {true};  // job-killed child
    std::unique_ptr<dxgi::INamedPipe> pipe;  // FramedPipe(WinPipe) used synchronously before async starts
    std::unique_ptr<dxgi::AsyncNamedPipe> async_pipe;
    // FramedPipe::send writes a length prefix then the payload. Concurrent
    // callers from Vibepollo's input dispatcher could interleave headers
    // with bodies; serialize all outbound sends through one mutex.
    std::mutex send_mutex;

    void send_locked(std::vector<std::uint8_t> bytes) {
      if (!async_pipe) {
        return;
      }
      std::lock_guard lock(send_mutex);
      async_pipe->send(bytes);
    }

    struct pad_state_t {
      feedback_queue_t feedback_queue;
      std::uint8_t client_relative_index {};
      gamepad_feedback_msg_t last_rumble {};
      gamepad_feedback_msg_t last_rgb_led {};
      bool last_rumble_valid {false};
      bool last_rgb_led_valid {false};
    };

    std::mutex pads_mutex;
    std::unordered_map<int, pad_state_t> pads;
    std::uint64_t sidecar_opcode_mask {0};
  };

  hidmaestro_t::hidmaestro_t():
      _impl(std::make_unique<impl_t>()) {
  }

  hidmaestro_t::~hidmaestro_t() {
    if (_impl) {
      if (_impl->async_pipe) {
        _impl->async_pipe->stop();
      }
      _impl->proc.terminate();
    }
  }

  int hidmaestro_t::init() {
    if (!is_hidmaestro_available()) {
      BOOST_LOG(debug) << "HIDMaestro sidecar not present; backend unavailable"sv;
      return 1;
    }

    auto exe = sidecar_path();
    if (exe.empty()) {
      BOOST_LOG(warning) << "Could not resolve HIDMaestro sidecar path"sv;
      return 1;
    }

    auto pipe_name = make_pipe_name();
    auto args = L"--pipe " + std::wstring(pipe_name.begin(), pipe_name.end());

    if (!_impl->proc.start(exe, args)) {
      BOOST_LOG(warning) << "Failed to launch HIDMaestro sidecar"sv;
      return 1;
    }

    // Give the sidecar a brief moment to create its named-pipe server before
    // we connect. The proper fix is a connect-with-retry; this matches the
    // existing display_settings_client cadence.
    dxgi::NamedPipeFactory factory;
    std::unique_ptr<dxgi::INamedPipe> raw;
    for (int attempt = 0; attempt < 20; ++attempt) {
      raw = factory.create_client(pipe_name);
      if (raw && raw->is_connected()) {
        break;
      }
      Sleep(50);
    }
    if (!raw || !raw->is_connected()) {
      BOOST_LOG(warning) << "Failed to connect to HIDMaestro sidecar pipe"sv;
      _impl->proc.terminate();
      return 1;
    }

    auto framed = std::make_unique<dxgi::FramedPipe>(std::move(raw));

    // Read the sidecar's HELLO synchronously to confirm protocol agreement
    // before we switch to async mode.
    std::array<std::uint8_t, 4096> rxbuf;
    std::size_t got {};
    auto res = framed->receive(rxbuf, got, kHelloTimeoutMs);
    if (res != dxgi::PipeResult::Success || got < 2) {
      BOOST_LOG(warning) << "Did not receive HELLO from HIDMaestro sidecar within "sv << kHelloTimeoutMs << "ms"sv;
      _impl->proc.terminate();
      return 1;
    }
    hidmaestro::Opcode op;
    std::uint8_t ver;
    if (!hidmaestro::peek_opcode({rxbuf.data(), got}, op, ver) || op != hidmaestro::Opcode::Hello) {
      BOOST_LOG(warning) << "First HIDMaestro frame is not HELLO"sv;
      _impl->proc.terminate();
      return 1;
    }
    hidmaestro::hello_t hello {};
    if (!hidmaestro::decode_hello({rxbuf.data(), got}, hello)) {
      BOOST_LOG(warning) << "Could not decode HELLO from HIDMaestro sidecar"sv;
      _impl->proc.terminate();
      return 1;
    }
    _impl->sidecar_opcode_mask = hello.supported_opcode_mask;

    // Reply with our HELLO advertising what we understand.
    const std::uint64_t host_mask =
      hidmaestro::opcode_bit(hidmaestro::Opcode::Hello) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::Alloc) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::Free) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::State) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::Touch) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::Motion) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::Battery) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::Rumble) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::Led) |
      hidmaestro::opcode_bit(hidmaestro::Opcode::TriggerEffect);
    auto host_hello = hidmaestro::encode_hello({hidmaestro::kProtocolVersion, host_mask, {}});
    if (!framed->send(host_hello, kSendTimeoutMs)) {
      BOOST_LOG(warning) << "Failed to send HELLO to HIDMaestro sidecar"sv;
      _impl->proc.terminate();
      return 1;
    }

    BOOST_LOG(info) << "HIDMaestro sidecar handshake complete; sidecar protocol v"sv
                    << (int) hello.protocol_version << ", profiles="sv << hello.supported_profiles.size();

    // Hand the framed pipe to AsyncNamedPipe so feedback frames pump on a
    // dedicated thread for the lifetime of this backend.
    auto async = std::make_unique<dxgi::AsyncNamedPipe>(std::move(framed));
    auto on_message = [this](std::span<const std::uint8_t> bytes) {
      hidmaestro::Opcode op;
      std::uint8_t ver;
      if (!hidmaestro::peek_opcode(bytes, op, ver)) {
        return;
      }
      switch (op) {
        case hidmaestro::Opcode::Rumble: {
          hidmaestro::rumble_t r {};
          if (!hidmaestro::decode_rumble(bytes, r)) {
            return;
          }
          std::lock_guard lock(_impl->pads_mutex);
          auto it = _impl->pads.find(r.pad_index);
          if (it == _impl->pads.end() || !it->second.feedback_queue) {
            return;
          }
          auto &pad = it->second;
          if (pad.last_rumble_valid && pad.last_rumble.data.rumble.lowfreq == r.lowfreq && pad.last_rumble.data.rumble.highfreq == r.highfreq) {
            return;
          }
          auto msg = gamepad_feedback_msg_t::make_rumble(pad.client_relative_index, r.lowfreq, r.highfreq);
          pad.feedback_queue->raise(msg);
          pad.last_rumble = msg;
          pad.last_rumble_valid = true;
          break;
        }
        case hidmaestro::Opcode::Led: {
          hidmaestro::led_t l {};
          if (!hidmaestro::decode_led(bytes, l)) {
            return;
          }
          std::lock_guard lock(_impl->pads_mutex);
          auto it = _impl->pads.find(l.pad_index);
          if (it == _impl->pads.end() || !it->second.feedback_queue) {
            return;
          }
          auto &pad = it->second;
          if (pad.last_rgb_led_valid && pad.last_rgb_led.data.rgb_led.r == l.r && pad.last_rgb_led.data.rgb_led.g == l.g && pad.last_rgb_led.data.rgb_led.b == l.b) {
            return;
          }
          auto msg = gamepad_feedback_msg_t::make_rgb_led(pad.client_relative_index, l.r, l.g, l.b);
          pad.feedback_queue->raise(msg);
          pad.last_rgb_led = msg;
          pad.last_rgb_led_valid = true;
          break;
        }
        case hidmaestro::Opcode::TriggerEffect: {
          gamepad_feedback_msg_t msg;
          if (hidmaestro::decode_trigger_effect(bytes, msg)) {
            std::lock_guard lock(_impl->pads_mutex);
            auto it = _impl->pads.find(msg.id);
            if (it != _impl->pads.end() && it->second.feedback_queue) {
              msg.id = it->second.client_relative_index;
              it->second.feedback_queue->raise(msg);
            }
          }
          break;
        }
        default:
          break;
      }
    };
    auto on_error = [](const std::string &err) {
      BOOST_LOG(warning) << "HIDMaestro pipe error: "sv << err;
    };
    auto on_broken = []() {
      BOOST_LOG(warning) << "HIDMaestro sidecar pipe broken; gamepad emulation will stop until restart"sv;
    };

    if (!async->start(on_message, on_error, on_broken)) {
      BOOST_LOG(warning) << "Failed to start HIDMaestro async pipe reader"sv;
      _impl->proc.terminate();
      return 1;
    }

    _impl->async_pipe = std::move(async);
    return 0;
  }

  int hidmaestro_t::alloc(const gamepad_id_t &id, const gamepad_arrival_t &metadata, feedback_queue_t feedback_queue) {
    if (!_impl->async_pipe) {
      return -1;
    }
    {
      std::lock_guard lock(_impl->pads_mutex);
      auto &pad = _impl->pads[id.globalIndex];
      pad.feedback_queue = std::move(feedback_queue);
      pad.client_relative_index = id.clientRelativeIndex;
      pad.last_rumble_valid = false;
      pad.last_rgb_led_valid = false;
    }
    hidmaestro::alloc_t a {
      .pad_index = static_cast<std::uint16_t>(id.globalIndex),
      .client_relative_index = id.clientRelativeIndex,
      .profile = select_profile(metadata),
      .client_type = metadata.type,
      .capabilities = metadata.capabilities,
      .supported_buttons = metadata.supportedButtons,
    };
    _impl->send_locked(hidmaestro::encode_alloc(a));
    return 0;
  }

  void hidmaestro_t::free_pad(int nr) {
    if (!_impl->async_pipe) {
      return;
    }
    {
      std::lock_guard lock(_impl->pads_mutex);
      _impl->pads.erase(nr);
    }
    _impl->send_locked(hidmaestro::encode_free({static_cast<std::uint16_t>(nr)}));
  }

  void hidmaestro_t::update(int nr, const gamepad_state_t &state) {
    if (!_impl->async_pipe) {
      return;
    }
    _impl->send_locked(hidmaestro::encode_state({static_cast<std::uint16_t>(nr), state}));
  }

  void hidmaestro_t::touch(const gamepad_touch_t &touch) {
    if (!_impl->async_pipe) {
      return;
    }
    hidmaestro::touch_t t {
      .pad_index = static_cast<std::uint16_t>(touch.id.globalIndex),
      .event_type = touch.eventType,
      .pointer_id = touch.pointerId,
      .x = touch.x,
      .y = touch.y,
      .pressure = touch.pressure,
    };
    _impl->send_locked(hidmaestro::encode_touch(t));
  }

  void hidmaestro_t::motion(const gamepad_motion_t &motion) {
    if (!_impl->async_pipe) {
      return;
    }
    hidmaestro::motion_t m {
      .pad_index = static_cast<std::uint16_t>(motion.id.globalIndex),
      .motion_type = motion.motionType,
      .x = motion.x,
      .y = motion.y,
      .z = motion.z,
    };
    _impl->send_locked(hidmaestro::encode_motion(m));
  }

  void hidmaestro_t::battery(const gamepad_battery_t &battery) {
    if (!_impl->async_pipe) {
      return;
    }
    hidmaestro::battery_t b {
      .pad_index = static_cast<std::uint16_t>(battery.id.globalIndex),
      .state = battery.state,
      .percentage = battery.percentage,
    };
    _impl->send_locked(hidmaestro::encode_battery(b));
  }

}  // namespace platf
