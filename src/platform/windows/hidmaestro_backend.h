/**
 * @file src/platform/windows/hidmaestro_backend.h
 * @brief HIDMaestro-backed virtual-gamepad backend.
 *
 * The host process talks to a sibling C# sidecar (`vibepollo_hidmaestro_host.exe`)
 * over a duplex named pipe. The sidecar links against `HIDMaestro.Core.dll` and
 * is responsible for creating system-wide virtual HID devices.
 */
#pragma once

#include <memory>

#include "gamepad_backend.h"

namespace platf {

  class hidmaestro_t: public gamepad_backend_t {
  public:
    hidmaestro_t();
    ~hidmaestro_t() override;

    int init() override;

    int alloc(const gamepad_id_t &id, const gamepad_arrival_t &metadata, feedback_queue_t feedback_queue) override;
    void free_pad(int nr) override;

    void update(int nr, const gamepad_state_t &state) override;
    void touch(const gamepad_touch_t &touch) override;
    void motion(const gamepad_motion_t &motion) override;
    void battery(const gamepad_battery_t &battery) override;

  private:
    struct impl_t;
    std::unique_ptr<impl_t> _impl;
  };

}  // namespace platf
