/**
 * @file src/platform/windows/hidmaestro_backend.cpp
 * @brief HIDMaestro-backed gamepad backend skeleton.
 *
 * This is the host-side counterpart of the C# sidecar at
 * `tools/hidmaestro_host/`. The sidecar wraps HIDMaestro.Core; this class
 * spawns it and forwards lifecycle / state / feedback frames over a duplex
 * named pipe using the existing FramedPipe / SelfHealingPipe / ProcessHandler
 * primitives under `src/platform/windows/ipc/`.
 *
 * Stage in progress: skeleton only. `init()` reports availability so the
 * `auto` backend selector in `input.cpp` can fall through to ViGEm cleanly
 * until the IPC client and sidecar land in follow-up commits.
 */

#include "hidmaestro_backend.h"

#include "misc.h"
#include "src/logging.h"

namespace platf {

  using namespace std::literals;

  struct hidmaestro_t::impl_t {
    // Will hold: IpcClient, ProcessHandler for the sidecar, per-pad state, and
    // a worker thread that translates incoming feedback frames into entries on
    // each pad's feedback_queue. Empty for now.
  };

  hidmaestro_t::hidmaestro_t():
      _impl(std::make_unique<impl_t>()) {
  }

  hidmaestro_t::~hidmaestro_t() = default;

  int hidmaestro_t::init() {
    // Probe availability. Today this only checks whether the sidecar binary is
    // bundled next to sunshine.exe; in a later commit it will also verify the
    // HIDMaestro UMDF2 driver is installed (or trigger an install via the
    // sidecar).
    if (!is_hidmaestro_available()) {
      BOOST_LOG(debug) << "HIDMaestro sidecar not present; backend unavailable"sv;
      return 1;
    }

    // Sidecar spawn + IPC handshake lands here. Until then, report failure so
    // the `auto` backend selector falls through to ViGEm.
    BOOST_LOG(info) << "HIDMaestro backend selected, but sidecar IPC is not yet implemented; falling back"sv;
    return 1;
  }

  int hidmaestro_t::alloc(const gamepad_id_t &id, const gamepad_arrival_t &metadata, feedback_queue_t feedback_queue) {
    (void) id;
    (void) metadata;
    (void) feedback_queue;
    return -1;
  }

  void hidmaestro_t::free_pad(int nr) {
    (void) nr;
  }

  void hidmaestro_t::update(int nr, const gamepad_state_t &state) {
    (void) nr;
    (void) state;
  }

  void hidmaestro_t::touch(const gamepad_touch_t &touch) {
    (void) touch;
  }

  void hidmaestro_t::motion(const gamepad_motion_t &motion) {
    (void) motion;
  }

  void hidmaestro_t::battery(const gamepad_battery_t &battery) {
    (void) battery;
  }

}  // namespace platf
