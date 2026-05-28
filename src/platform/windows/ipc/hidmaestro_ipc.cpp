/**
 * @file src/platform/windows/ipc/hidmaestro_ipc.cpp
 * @brief Wire-format codec for the HIDMaestro sidecar IPC.
 *
 * The encoders write little-endian fields with no padding. The decoders mirror
 * that and reject frames whose declared structure doesn't match what they read
 * — a malformed frame is dropped and logged by the caller; the pipe layer
 * already resyncs on framing errors.
 */

#include "hidmaestro_ipc.h"

#include <algorithm>
#include <cstring>

namespace platf::hidmaestro {

  namespace {

    void put_u8(std::vector<std::uint8_t> &out, std::uint8_t v) {
      out.push_back(v);
    }

    void put_u16(std::vector<std::uint8_t> &out, std::uint16_t v) {
      out.push_back(static_cast<std::uint8_t>(v & 0xFF));
      out.push_back(static_cast<std::uint8_t>((v >> 8) & 0xFF));
    }

    void put_u32(std::vector<std::uint8_t> &out, std::uint32_t v) {
      put_u16(out, static_cast<std::uint16_t>(v & 0xFFFF));
      put_u16(out, static_cast<std::uint16_t>((v >> 16) & 0xFFFF));
    }

    void put_u64(std::vector<std::uint8_t> &out, std::uint64_t v) {
      put_u32(out, static_cast<std::uint32_t>(v & 0xFFFFFFFFu));
      put_u32(out, static_cast<std::uint32_t>((v >> 32) & 0xFFFFFFFFu));
    }

    void put_i16(std::vector<std::uint8_t> &out, std::int16_t v) {
      put_u16(out, static_cast<std::uint16_t>(v));
    }

    void put_f32(std::vector<std::uint8_t> &out, float v) {
      std::uint32_t bits;
      std::memcpy(&bits, &v, sizeof(bits));
      put_u32(out, bits);
    }

    void put_string(std::vector<std::uint8_t> &out, std::string_view s) {
      put_u16(out, static_cast<std::uint16_t>(s.size()));
      out.insert(out.end(), s.begin(), s.end());
    }

    void header(std::vector<std::uint8_t> &out, Opcode op) {
      put_u8(out, static_cast<std::uint8_t>(op));
      put_u8(out, kProtocolVersion);
    }

    // Lightweight cursor over an inbound frame's payload bytes.
    struct cursor_t {
      std::span<const std::uint8_t> bytes;
      std::size_t pos {0};

      bool need(std::size_t n) const {
        return pos + n <= bytes.size();
      }

      bool read_u8(std::uint8_t &v) {
        if (!need(1)) {
          return false;
        }
        v = bytes[pos++];
        return true;
      }

      bool read_u16(std::uint16_t &v) {
        if (!need(2)) {
          return false;
        }
        v = static_cast<std::uint16_t>(bytes[pos]) | (static_cast<std::uint16_t>(bytes[pos + 1]) << 8);
        pos += 2;
        return true;
      }

      bool read_u32(std::uint32_t &v) {
        std::uint16_t lo;
        std::uint16_t hi;
        if (!read_u16(lo) || !read_u16(hi)) {
          return false;
        }
        v = static_cast<std::uint32_t>(lo) | (static_cast<std::uint32_t>(hi) << 16);
        return true;
      }
    };

  }  // namespace

  std::vector<std::uint8_t> encode_hello(const hello_t &hello) {
    std::vector<std::uint8_t> out;
    header(out, Opcode::Hello);
    put_u8(out, hello.protocol_version);
    put_u64(out, hello.supported_opcode_mask);
    put_u16(out, static_cast<std::uint16_t>(hello.supported_profiles.size()));
    for (const auto &p : hello.supported_profiles) {
      put_string(out, p);
    }
    return out;
  }

  std::vector<std::uint8_t> encode_alloc(const alloc_t &a) {
    std::vector<std::uint8_t> out;
    header(out, Opcode::Alloc);
    put_u16(out, a.pad_index);
    put_u8(out, a.client_relative_index);
    put_string(out, a.profile);
    put_u8(out, a.client_type);
    put_u16(out, a.capabilities);
    put_u32(out, a.supported_buttons);
    return out;
  }

  std::vector<std::uint8_t> encode_free(const free_t &f) {
    std::vector<std::uint8_t> out;
    header(out, Opcode::Free);
    put_u16(out, f.pad_index);
    return out;
  }

  std::vector<std::uint8_t> encode_state(const state_t &s) {
    std::vector<std::uint8_t> out;
    header(out, Opcode::State);
    put_u16(out, s.pad_index);
    put_u32(out, s.state.buttonFlags);
    put_u8(out, s.state.lt);
    put_u8(out, s.state.rt);
    put_i16(out, s.state.lsX);
    put_i16(out, s.state.lsY);
    put_i16(out, s.state.rsX);
    put_i16(out, s.state.rsY);
    return out;
  }

  std::vector<std::uint8_t> encode_touch(const touch_t &t) {
    std::vector<std::uint8_t> out;
    header(out, Opcode::Touch);
    put_u16(out, t.pad_index);
    put_u8(out, t.event_type);
    put_u32(out, t.pointer_id);
    put_f32(out, t.x);
    put_f32(out, t.y);
    put_f32(out, t.pressure);
    return out;
  }

  std::vector<std::uint8_t> encode_motion(const motion_t &m) {
    std::vector<std::uint8_t> out;
    header(out, Opcode::Motion);
    put_u16(out, m.pad_index);
    put_u8(out, m.motion_type);
    put_f32(out, m.x);
    put_f32(out, m.y);
    put_f32(out, m.z);
    return out;
  }

  std::vector<std::uint8_t> encode_battery(const battery_t &b) {
    std::vector<std::uint8_t> out;
    header(out, Opcode::Battery);
    put_u16(out, b.pad_index);
    put_u8(out, b.state);
    put_u8(out, b.percentage);
    return out;
  }

  bool peek_opcode(std::span<const std::uint8_t> frame, Opcode &out_opcode, std::uint8_t &out_version) {
    if (frame.size() < 2) {
      return false;
    }
    out_opcode = static_cast<Opcode>(frame[0]);
    out_version = frame[1];
    return true;
  }

  bool decode_hello(std::span<const std::uint8_t> frame, hello_t &out) {
    if (frame.size() < 2) {
      return false;
    }
    cursor_t c {frame.subspan(2)};
    std::uint16_t profile_count;
    if (!c.read_u8(out.protocol_version)) {
      return false;
    }
    std::uint64_t mask_lo;
    std::uint32_t mask_hi_u32;
    {
      std::uint32_t lo;
      std::uint32_t hi;
      if (!c.read_u32(lo) || !c.read_u32(hi)) {
        return false;
      }
      mask_lo = lo;
      mask_hi_u32 = hi;
    }
    out.supported_opcode_mask = mask_lo | (static_cast<std::uint64_t>(mask_hi_u32) << 32);
    if (!c.read_u16(profile_count)) {
      return false;
    }
    out.supported_profiles.clear();
    out.supported_profiles.reserve(profile_count);
    for (std::uint16_t i = 0; i < profile_count; ++i) {
      std::uint16_t len;
      if (!c.read_u16(len) || !c.need(len)) {
        return false;
      }
      out.supported_profiles.emplace_back(reinterpret_cast<const char *>(&c.bytes[c.pos]), len);
      c.pos += len;
    }
    return true;
  }

  bool decode_rumble(std::span<const std::uint8_t> frame, rumble_t &out) {
    if (frame.size() < 2) {
      return false;
    }
    cursor_t c {frame.subspan(2)};
    return c.read_u16(out.pad_index) && c.read_u16(out.lowfreq) && c.read_u16(out.highfreq);
  }

  bool decode_led(std::span<const std::uint8_t> frame, led_t &out) {
    if (frame.size() < 2) {
      return false;
    }
    cursor_t c {frame.subspan(2)};
    return c.read_u16(out.pad_index) && c.read_u8(out.r) && c.read_u8(out.g) && c.read_u8(out.b);
  }

  bool decode_trigger_effect(std::span<const std::uint8_t> frame, gamepad_feedback_msg_t &out) {
    if (frame.size() < 2) {
      return false;
    }
    cursor_t c {frame.subspan(2)};
    std::uint16_t pad_index;
    std::uint8_t event_flags;
    std::uint8_t type_left;
    std::uint8_t type_right;
    if (!c.read_u16(pad_index) || !c.read_u8(event_flags) || !c.read_u8(type_left) || !c.read_u8(type_right)) {
      return false;
    }
    std::array<std::uint8_t, 10> left {};
    std::array<std::uint8_t, 10> right {};
    if (!c.need(20)) {
      return false;
    }
    std::copy_n(&c.bytes[c.pos], 10, left.begin());
    c.pos += 10;
    std::copy_n(&c.bytes[c.pos], 10, right.begin());
    c.pos += 10;
    out = gamepad_feedback_msg_t::make_adaptive_triggers(pad_index, event_flags, type_left, type_right, left, right);
    return true;
  }

}  // namespace platf::hidmaestro
