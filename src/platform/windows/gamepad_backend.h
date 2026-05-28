/**
 * @file src/platform/windows/gamepad_backend.h
 * @brief Abstract virtual-gamepad backend interface for the Windows input layer.
 */
#pragma once

#include "src/platform/common.h"

namespace platf {

  // Backend-agnostic virtual-controller emulation interface. The Windows input
  // layer holds one implementation per process (ViGEmBus today, HIDMaestro next)
  // and dispatches all per-pad lifecycle and update calls through it.
  //
  // Each backend owns its own per-pad storage. Indices passed to update/free are
  // the global gamepad index allocated by src/input.cpp and must round-trip
  // unchanged through the backend.
  class gamepad_backend_t {
  public:
    virtual ~gamepad_backend_t() = default;

    virtual int init() = 0;

    virtual int alloc(const gamepad_id_t &id, const gamepad_arrival_t &metadata, feedback_queue_t feedback_queue) = 0;
    virtual void free_pad(int nr) = 0;

    virtual void update(int nr, const gamepad_state_t &state) = 0;
    virtual void touch(const gamepad_touch_t &touch) = 0;
    virtual void motion(const gamepad_motion_t &motion) = 0;
    virtual void battery(const gamepad_battery_t &battery) = 0;
  };

}  // namespace platf
