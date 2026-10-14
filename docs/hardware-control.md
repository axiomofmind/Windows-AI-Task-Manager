# NVIDIA hardware-control boundary

AI Task Manager limits hardware writes to NVIDIA GPU-local power and experimental GPU-local fan operations. Lenovo chassis, CPU, pump, motherboard, and embedded-controller writes are out of scope.

## Power-limit operations

The read-only collector obtains the requested, enforced, default, minimum, and maximum power limits from the driver. A requested value is accepted only when it is within the range returned for the same GPU UUID. The elevated helper exposes one positional, allow-listed operation, applies the value with `nvmlDeviceSetPowerManagementLimit`, and reads it back with `nvmlDeviceGetPowerManagementLimit` before exiting.

Browser requests require the random in-memory action token, same-origin validation, an idempotency key, and a confirmation phrase containing the target UUID suffix and wattage. Every attempted helper operation receives an audit ID and the result is stored in SQLite. Public defaults expose the controls, but no operation executes without deliberate confirmation and Windows administrator approval.

Official references:

- [NVIDIA NVML device queries](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html)
- [NVIDIA NVML device commands](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceCommands.html)
- [NVIDIA System Management Interface](https://docs.nvidia.com/deploy/nvidia-smi/index.html)

## Experimental NVIDIA GPU fan control

The collector uses documented NVML queries: `nvmlDeviceGetNumFans`, `nvmlDeviceGetFanSpeed_v2`, `nvmlDeviceGetTargetFanSpeed`, `nvmlDeviceGetMinMaxFanSpeed`, `nvmlDeviceGetFanControlPolicy_v2`, and the slowdown/shutdown thermal-threshold query. Unsupported and permission-denied results remain explicit capability states. Discovery never changes fan state.

Multiple physical fans may share one controllable channel. A channel count is therefore not a promise that every visible fan is independently controllable.

The experimental manual controller is visible in public defaults but remains inactive until the user starts a session. After the safety acknowledgement and Windows administrator approval, a separately elevated, UUID-bound helper owns temperature polling, policy evaluation, fan writes, verification, and a 3–15 second lease. The requested duty is a minimum: the helper ramps toward the device maximum as temperature approaches the lesser of the configured failsafe and five degrees below the device slowdown threshold. A missing defensible threshold prevents enablement.

The helper calls `nvmlDeviceSetFanSpeed_v2` for every enumerated channel and verifies the manual policy and target. It calls `nvmlDeviceSetDefaultFanSpeed_v2` on requested disable, lease expiry, parent exit, IPC loss, temperature-read failure, write failure, or verification failure. The host also exposes an independent elevated automatic-restore command for a manual policy that no longer has an active session.

Starting a manual session requires the random action token, a GUID idempotency key, an explicit acknowledgement that competing fan controllers are closed, a deliberate start action, and one Windows administrator approval. After the UUID-bound watchdog session is active, minimum-duty updates reuse its authenticated pipe and do not require repeated text entry or elevation. Every start, update, restore, rejection, and watchdog transition remains audited in SQLite. If the session disappears between display and update, an unacknowledged request is rejected rather than silently starting a new elevated session.

Important limitation: NVIDIA documents that setting fan speed transfers thermal-management responsibility to the caller. Normal disconnect and host-crash paths are lease-protected, and the helper records a persistent ownership journal so a later controlled start restores automatic policy before reclaiming the GPU. Windows still cannot guarantee immediate restoration after forceful helper termination, power loss, kernel failure, or a hardware/driver failure; Windows Service Control Manager recovery is not implemented. Treat this mode as experimental and keep independent temperature monitoring during qualification.

This controls NVIDIA GPU-local channels only. It does not control Lenovo P8 chassis, CPU, pump, motherboard, or embedded-controller fans.

Official references:

- [NVIDIA NVML device queries](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html)
- [NVIDIA NVML device commands](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceCommands.html)
- [NVIDIA NVML enums](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceEnums.html)
- [NVIDIA NVML field-value enums](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlFieldValueEnums.html)
