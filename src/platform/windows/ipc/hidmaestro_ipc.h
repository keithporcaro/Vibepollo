/**
 * @file src/platform/windows/ipc/hidmaestro_ipc.h
 * @brief Wire-format codec for the HIDMaestro sidecar IPC.
 *
 * Frames travel inside `FramedPipe` length-prefixed envelopes. Each frame is:
 *
 *   [u8 opcode][u8 version][payload bytes]
 *
 * The codec is **opcoded, length-prefixed, and versioned** so that new opcodes
 * (`STATE_EX`, `TRACKPAD`, `HAPTIC`) and new trailing payload fields land
 * additively without breaking either side. Both sides negotiate the
 * intersection of supported opcodes during `HELLO`.
 *
 * The C# sidecar at `tools/hidmaestro_host/` mirrors this layout exactly.
 */
#pragma once

#include <cstdint>
#include <span>
#include <string>
#include <string_view>
#include <vector>

#include "src/platform/common.h"

namespace platf::hidmaestro {

  // Bump when an existing frame's layout changes incompatibly. Adding a new
  // opcode or appending trailing bytes to an existing frame does NOT require a
  // bump — peers negotiate the intersection at HELLO.
  constexpr std::uint8_t kProtocolVersion = 1;

  enum class Opcode : std::uint8_t {
    // Host → sidecar
    Hello = 0x00,
    Alloc = 0x01,
    Free = 0x02,
    State = 0x03,
    Touch = 0x04,
    Motion = 0x05,
    Battery = 0x06,

    // Reserved for the Steam Controller / SC-class client extension. Not
    // emitted today; ABI fixed when moonlight-common-c grows the corresponding
    // packets.
    StateEx = 0x10,
    Trackpad = 0x11,

    // Sidecar → host
    Rumble = 0x80,
    Led = 0x81,
    TriggerEffect = 0x82,

    // Reserved for SC linear-actuator haptics (distinct shape from rumble).
    Haptic = 0x90,
  };

  // Bit positions used inside HELLO's supported_opcode_mask. The mask covers
  // both directions of traffic; each peer sets the bits it understands.
  constexpr std::uint64_t opcode_bit(Opcode op) {
    return std::uint64_t {1} << static_cast<std::uint8_t>(op);
  }

  struct hello_t {
    std::uint8_t protocol_version;
    std::uint64_t supported_opcode_mask;
    std::vector<std::string> supported_profiles;  // empty from the host side
  };

  struct alloc_t {
    std::uint16_t pad_index;  // gamepad_id_t::globalIndex
    std::uint8_t client_relative_index;  // gamepad_id_t::clientRelativeIndex
    std::string profile;  // e.g. "x360", "ds4", "steam-controller-2026"
    std::uint8_t client_type;  // LI_CTYPE_*
    std::uint16_t capabilities;  // LI_CCAP_*
    std::uint32_t supported_buttons;
  };

  struct free_t {
    std::uint16_t pad_index;
  };

  struct state_t {
    std::uint16_t pad_index;
    gamepad_state_t state;
  };

  struct touch_t {
    std::uint16_t pad_index;
    std::uint8_t event_type;
    std::uint32_t pointer_id;
    float x;
    float y;
    float pressure;
  };

  struct motion_t {
    std::uint16_t pad_index;
    std::uint8_t motion_type;
    float x;
    float y;
    float z;
  };

  struct battery_t {
    std::uint16_t pad_index;
    std::uint8_t state;
    std::uint8_t percentage;
  };

  struct rumble_t {
    std::uint16_t pad_index;
    std::uint16_t lowfreq;
    std::uint16_t highfreq;
  };

  struct led_t {
    std::uint16_t pad_index;
    std::uint8_t r;
    std::uint8_t g;
    std::uint8_t b;
  };

  // Encode an outbound frame for the sidecar. Returns the bytes to hand to
  // `FramedPipe::send()` — the framing length prefix is added by FramedPipe.
  std::vector<std::uint8_t> encode_hello(const hello_t &hello);
  std::vector<std::uint8_t> encode_alloc(const alloc_t &alloc);
  std::vector<std::uint8_t> encode_free(const free_t &free);
  std::vector<std::uint8_t> encode_state(const state_t &state);
  std::vector<std::uint8_t> encode_touch(const touch_t &touch);
  std::vector<std::uint8_t> encode_motion(const motion_t &motion);
  std::vector<std::uint8_t> encode_battery(const battery_t &battery);

  // Decode an inbound frame from the sidecar. The caller has already stripped
  // FramedPipe's length prefix and supplies the [opcode][version][payload]
  // slice. Returns false on malformed input; the caller should log and skip.
  bool peek_opcode(std::span<const std::uint8_t> frame, Opcode &out_opcode, std::uint8_t &out_version);
  bool decode_hello(std::span<const std::uint8_t> frame, hello_t &out);
  bool decode_rumble(std::span<const std::uint8_t> frame, rumble_t &out);
  bool decode_led(std::span<const std::uint8_t> frame, led_t &out);
  bool decode_trigger_effect(std::span<const std::uint8_t> frame, gamepad_feedback_msg_t &out);

}  // namespace platf::hidmaestro
