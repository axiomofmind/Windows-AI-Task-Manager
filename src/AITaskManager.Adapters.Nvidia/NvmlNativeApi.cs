using System.Runtime.InteropServices;
using System.Text;

namespace AITaskManager.Adapters.Nvidia;

public enum NvmlResult
{
    Success = 0,
    Uninitialized = 1,
    InvalidArgument = 2,
    NotSupported = 3,
    NoPermission = 4,
    AlreadyInitialized = 5,
    NotFound = 6,
    InsufficientSize = 7,
    InsufficientPower = 8,
    DriverNotLoaded = 9,
    Timeout = 10,
    IrqIssue = 11,
    LibraryNotFound = 12,
    FunctionNotFound = 13,
    CorruptedInfoRom = 14,
    GpuIsLost = 15,
    ResetRequired = 16,
    OperatingSystem = 17,
    LibraryRmVersionMismatch = 18,
    InUse = 19,
    Memory = 20,
    NoData = 21,
    VgpuEccNotSupported = 22,
    InsufficientResources = 23,
    FrequencyNotSupported = 24,
    ArgumentVersionMismatch = 25,
    Deprecated = 26,
    Unknown = 999,
}

public readonly record struct NvmlMemoryInfo(ulong Total, ulong Free, ulong Used);

public readonly record struct NvmlUtilizationInfo(uint Gpu, uint Memory);

public readonly record struct NvmlProcessInfo(uint ProcessId, ulong UsedGpuMemory);

public interface INvmlApi : IDisposable
{
    NvmlResult Initialize();

    NvmlResult Shutdown();

    NvmlResult GetDeviceCount(out uint count);

    NvmlResult GetDeviceHandle(uint index, out nint device);

    NvmlResult GetDeviceUuid(nint device, out string value);

    NvmlResult GetDeviceName(nint device, out string value);

    NvmlResult GetMemoryInfo(nint device, out NvmlMemoryInfo value);

    NvmlResult GetUtilization(nint device, out NvmlUtilizationInfo value);

    NvmlResult GetTemperature(nint device, out uint value);

    NvmlResult GetTemperatureThreshold(nint device, NvmlTemperatureThreshold threshold, out uint value);

    NvmlResult GetPowerUsage(nint device, out uint value);

    NvmlResult GetPowerLimit(nint device, out uint value);

    NvmlResult GetRequestedPowerLimit(nint device, out uint value);

    NvmlResult GetDefaultPowerLimit(nint device, out uint value);

    NvmlResult GetPowerLimitConstraints(nint device, out uint minimum, out uint maximum);

    NvmlResult SetPowerLimit(nint device, uint value);

    NvmlResult GetFanSpeed(nint device, out uint value);

    NvmlResult GetFanCount(nint device, out uint value);

    NvmlResult GetFanSpeed(nint device, uint fanIndex, out uint value);

    NvmlResult GetTargetFanSpeed(nint device, uint fanIndex, out uint value);

    NvmlResult GetFanControlPolicy(nint device, uint fanIndex, out uint value);

    NvmlResult GetFanSpeedConstraints(nint device, out uint minimum, out uint maximum);

    NvmlResult SetFanSpeed(nint device, uint fanIndex, uint value);

    NvmlResult RestoreDefaultFanSpeed(nint device, uint fanIndex);

    NvmlResult GetClock(nint device, NvmlClockType clockType, out uint value);

    NvmlResult GetDriverVersion(out string value);

    NvmlResult GetLibraryVersion(out string value);

    NvmlResult GetComputeProcesses(nint device, out IReadOnlyList<NvmlProcessInfo> processes);

    NvmlResult GetGraphicsProcesses(nint device, out IReadOnlyList<NvmlProcessInfo> processes);
}

public enum NvmlClockType : uint
{
    Graphics = 0,
    StreamingMultiprocessor = 1,
    Memory = 2,
    Video = 3,
}

public enum NvmlTemperatureThreshold : uint
{
    Shutdown = 0,
    Slowdown = 1,
}

internal sealed class NvmlNativeApi : INvmlApi
{
    private const int DeviceNameBufferSize = 96;
    private const int UuidBufferSize = 96;
    private const int VersionBufferSize = 96;

    private readonly nint _library;
    private readonly InitDelegate _initialize;
    private readonly ShutdownDelegate _shutdown;
    private readonly DeviceGetCountDelegate _getDeviceCount;
    private readonly DeviceGetHandleDelegate _getDeviceHandle;
    private readonly DeviceGetStringDelegate _getDeviceUuid;
    private readonly DeviceGetStringDelegate _getDeviceName;
    private readonly DeviceGetMemoryInfoDelegate _getMemoryInfo;
    private readonly DeviceGetUtilizationDelegate _getUtilization;
    private readonly DeviceGetTemperatureDelegate _getTemperature;
    private readonly DeviceGetIndexedUIntDelegate? _getTemperatureThreshold;
    private readonly DeviceGetUIntDelegate _getPowerUsage;
    private readonly DeviceGetUIntDelegate _getPowerLimit;
    private readonly DeviceGetUIntDelegate? _getRequestedPowerLimit;
    private readonly DeviceGetUIntDelegate? _getDefaultPowerLimit;
    private readonly DeviceGetTwoUIntDelegate? _getPowerLimitConstraints;
    private readonly DeviceSetUIntDelegate? _setPowerLimit;
    private readonly DeviceGetUIntDelegate _getFanSpeed;
    private readonly DeviceGetUIntDelegate? _getFanCount;
    private readonly DeviceGetIndexedUIntDelegate? _getFanSpeedV2;
    private readonly DeviceGetIndexedUIntDelegate? _getTargetFanSpeed;
    private readonly DeviceGetIndexedUIntDelegate? _getFanControlPolicy;
    private readonly DeviceGetTwoUIntDelegate? _getFanSpeedConstraints;
    private readonly DeviceSetIndexedUIntDelegate? _setFanSpeed;
    private readonly DeviceSetIndexDelegate? _restoreDefaultFanSpeed;
    private readonly DeviceGetClockDelegate _getClock;
    private readonly SystemGetStringDelegate _getDriverVersion;
    private readonly SystemGetStringDelegate _getLibraryVersion;
    private readonly DeviceGetProcessesDelegate? _getComputeProcesses;
    private readonly DeviceGetProcessesDelegate? _getGraphicsProcesses;
    private bool _disposed;

    private NvmlNativeApi(nint library)
    {
        _library = library;
        _initialize = Bind<InitDelegate>("nvmlInit_v2");
        _shutdown = Bind<ShutdownDelegate>("nvmlShutdown");
        _getDeviceCount = Bind<DeviceGetCountDelegate>("nvmlDeviceGetCount_v2");
        _getDeviceHandle = Bind<DeviceGetHandleDelegate>("nvmlDeviceGetHandleByIndex_v2");
        _getDeviceUuid = Bind<DeviceGetStringDelegate>("nvmlDeviceGetUUID");
        _getDeviceName = Bind<DeviceGetStringDelegate>("nvmlDeviceGetName");
        _getMemoryInfo = Bind<DeviceGetMemoryInfoDelegate>("nvmlDeviceGetMemoryInfo");
        _getUtilization = Bind<DeviceGetUtilizationDelegate>("nvmlDeviceGetUtilizationRates");
        _getTemperature = Bind<DeviceGetTemperatureDelegate>("nvmlDeviceGetTemperature");
        _getTemperatureThreshold = TryBind<DeviceGetIndexedUIntDelegate>("nvmlDeviceGetTemperatureThreshold");
        _getPowerUsage = Bind<DeviceGetUIntDelegate>("nvmlDeviceGetPowerUsage");
        _getPowerLimit = Bind<DeviceGetUIntDelegate>("nvmlDeviceGetEnforcedPowerLimit");
        _getRequestedPowerLimit = TryBind<DeviceGetUIntDelegate>("nvmlDeviceGetPowerManagementLimit");
        _getDefaultPowerLimit = TryBind<DeviceGetUIntDelegate>("nvmlDeviceGetPowerManagementDefaultLimit");
        _getPowerLimitConstraints = TryBind<DeviceGetTwoUIntDelegate>("nvmlDeviceGetPowerManagementLimitConstraints");
        _setPowerLimit = TryBind<DeviceSetUIntDelegate>("nvmlDeviceSetPowerManagementLimit");
        _getFanSpeed = Bind<DeviceGetUIntDelegate>("nvmlDeviceGetFanSpeed");
        _getFanCount = TryBind<DeviceGetUIntDelegate>("nvmlDeviceGetNumFans");
        _getFanSpeedV2 = TryBind<DeviceGetIndexedUIntDelegate>("nvmlDeviceGetFanSpeed_v2");
        _getTargetFanSpeed = TryBind<DeviceGetIndexedUIntDelegate>("nvmlDeviceGetTargetFanSpeed");
        _getFanControlPolicy = TryBind<DeviceGetIndexedUIntDelegate>("nvmlDeviceGetFanControlPolicy_v2");
        _getFanSpeedConstraints = TryBind<DeviceGetTwoUIntDelegate>("nvmlDeviceGetMinMaxFanSpeed");
        _setFanSpeed = TryBind<DeviceSetIndexedUIntDelegate>("nvmlDeviceSetFanSpeed_v2");
        _restoreDefaultFanSpeed = TryBind<DeviceSetIndexDelegate>("nvmlDeviceSetDefaultFanSpeed_v2");
        _getClock = Bind<DeviceGetClockDelegate>("nvmlDeviceGetClockInfo");
        _getDriverVersion = Bind<SystemGetStringDelegate>("nvmlSystemGetDriverVersion");
        _getLibraryVersion = Bind<SystemGetStringDelegate>("nvmlSystemGetNVMLVersion");
        _getComputeProcesses = TryBind<DeviceGetProcessesDelegate>("nvmlDeviceGetComputeRunningProcesses_v3");
        _getGraphicsProcesses = TryBind<DeviceGetProcessesDelegate>("nvmlDeviceGetGraphicsRunningProcesses_v3");
    }

    public static bool TryLoad(out INvmlApi? api, out string? error)
    {
        api = null;
        error = null;

        if (!OperatingSystem.IsWindows())
        {
            error = "NVML loading is currently implemented for Windows only.";
            return false;
        }

        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "nvml.dll"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "NVIDIA Corporation",
                "NVSMI",
                "nvml.dll"),
            "nvml.dll",
        };

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!NativeLibrary.TryLoad(candidate, out var library))
            {
                continue;
            }

            try
            {
                api = new NvmlNativeApi(library);
                return true;
            }
            catch (Exception exception) when (exception is EntryPointNotFoundException or InvalidCastException)
            {
                NativeLibrary.Free(library);
                error = $"The driver NVML library is missing a required documented entry point: {exception.Message}";
                return false;
            }
        }

        error = "The NVIDIA driver-installed nvml.dll could not be located.";
        return false;
    }

    public NvmlResult Initialize() => _initialize();

    public NvmlResult Shutdown() => _shutdown();

    public NvmlResult GetDeviceCount(out uint count) => _getDeviceCount(out count);

    public NvmlResult GetDeviceHandle(uint index, out nint device) => _getDeviceHandle(index, out device);

    public NvmlResult GetDeviceUuid(nint device, out string value) =>
        GetDeviceString(_getDeviceUuid, device, UuidBufferSize, out value);

    public NvmlResult GetDeviceName(nint device, out string value) =>
        GetDeviceString(_getDeviceName, device, DeviceNameBufferSize, out value);

    public NvmlResult GetMemoryInfo(nint device, out NvmlMemoryInfo value)
    {
        var result = _getMemoryInfo(device, out var native);
        value = new NvmlMemoryInfo(native.Total, native.Free, native.Used);
        return result;
    }

    public NvmlResult GetUtilization(nint device, out NvmlUtilizationInfo value)
    {
        var result = _getUtilization(device, out var native);
        value = new NvmlUtilizationInfo(native.Gpu, native.Memory);
        return result;
    }

    public NvmlResult GetTemperature(nint device, out uint value) =>
        _getTemperature(device, sensorType: 0, out value);

    public NvmlResult GetTemperatureThreshold(
        nint device,
        NvmlTemperatureThreshold threshold,
        out uint value) => Invoke(_getTemperatureThreshold, device, (uint)threshold, out value);

    public NvmlResult GetPowerUsage(nint device, out uint value) => _getPowerUsage(device, out value);

    public NvmlResult GetPowerLimit(nint device, out uint value) => _getPowerLimit(device, out value);

    public NvmlResult GetRequestedPowerLimit(nint device, out uint value) =>
        Invoke(_getRequestedPowerLimit, device, out value);

    public NvmlResult GetDefaultPowerLimit(nint device, out uint value) =>
        Invoke(_getDefaultPowerLimit, device, out value);

    public NvmlResult GetPowerLimitConstraints(nint device, out uint minimum, out uint maximum) =>
        Invoke(_getPowerLimitConstraints, device, out minimum, out maximum);

    public NvmlResult SetPowerLimit(nint device, uint value) =>
        _setPowerLimit?.Invoke(device, value) ?? NvmlResult.FunctionNotFound;

    public NvmlResult GetFanSpeed(nint device, out uint value) => _getFanSpeed(device, out value);

    public NvmlResult GetFanCount(nint device, out uint value) => Invoke(_getFanCount, device, out value);

    public NvmlResult GetFanSpeed(nint device, uint fanIndex, out uint value) =>
        Invoke(_getFanSpeedV2, device, fanIndex, out value);

    public NvmlResult GetTargetFanSpeed(nint device, uint fanIndex, out uint value) =>
        Invoke(_getTargetFanSpeed, device, fanIndex, out value);

    public NvmlResult GetFanControlPolicy(nint device, uint fanIndex, out uint value) =>
        Invoke(_getFanControlPolicy, device, fanIndex, out value);

    public NvmlResult GetFanSpeedConstraints(nint device, out uint minimum, out uint maximum) =>
        Invoke(_getFanSpeedConstraints, device, out minimum, out maximum);

    public NvmlResult SetFanSpeed(nint device, uint fanIndex, uint value) =>
        _setFanSpeed?.Invoke(device, fanIndex, value) ?? NvmlResult.FunctionNotFound;

    public NvmlResult RestoreDefaultFanSpeed(nint device, uint fanIndex) =>
        _restoreDefaultFanSpeed?.Invoke(device, fanIndex) ?? NvmlResult.FunctionNotFound;

    public NvmlResult GetClock(nint device, NvmlClockType clockType, out uint value) =>
        _getClock(device, clockType, out value);

    public NvmlResult GetDriverVersion(out string value) =>
        GetSystemString(_getDriverVersion, VersionBufferSize, out value);

    public NvmlResult GetLibraryVersion(out string value) =>
        GetSystemString(_getLibraryVersion, VersionBufferSize, out value);

    public NvmlResult GetComputeProcesses(nint device, out IReadOnlyList<NvmlProcessInfo> processes) =>
        GetProcesses(_getComputeProcesses, device, out processes);

    public NvmlResult GetGraphicsProcesses(nint device, out IReadOnlyList<NvmlProcessInfo> processes) =>
        GetProcesses(_getGraphicsProcesses, device, out processes);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeLibrary.Free(_library);
    }

    private T Bind<T>(string exportName)
        where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, exportName));

    private T? TryBind<T>(string exportName)
        where T : Delegate => NativeLibrary.TryGetExport(_library, exportName, out var address)
            ? Marshal.GetDelegateForFunctionPointer<T>(address)
            : null;

    private static NvmlResult Invoke(DeviceGetUIntDelegate? getter, nint device, out uint value)
    {
        value = 0;
        return getter?.Invoke(device, out value) ?? NvmlResult.FunctionNotFound;
    }

    private static NvmlResult Invoke(
        DeviceGetIndexedUIntDelegate? getter,
        nint device,
        uint index,
        out uint value)
    {
        value = 0;
        return getter?.Invoke(device, index, out value) ?? NvmlResult.FunctionNotFound;
    }

    private static NvmlResult Invoke(
        DeviceGetTwoUIntDelegate? getter,
        nint device,
        out uint first,
        out uint second)
    {
        first = 0;
        second = 0;
        return getter?.Invoke(device, out first, out second) ?? NvmlResult.FunctionNotFound;
    }

    private static NvmlResult GetProcesses(
        DeviceGetProcessesDelegate? getter,
        nint device,
        out IReadOnlyList<NvmlProcessInfo> processes)
    {
        processes = [];
        if (getter is null)
        {
            return NvmlResult.FunctionNotFound;
        }

        uint count = 0;
        var result = getter(device, ref count, 0);
        if (result == NvmlResult.Success && count == 0)
        {
            return NvmlResult.Success;
        }

        if (result != NvmlResult.InsufficientSize || count == 0)
        {
            return result;
        }

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var capacity = checked(count + 8);
            var rowSize = Marshal.SizeOf<NvmlProcessInfoNative>();
            var buffer = Marshal.AllocHGlobal(checked((int)capacity * rowSize));
            try
            {
                count = capacity;
                result = getter(device, ref count, buffer);
                if (result == NvmlResult.InsufficientSize)
                {
                    continue;
                }

                if (result != NvmlResult.Success)
                {
                    return result;
                }

                var values = new List<NvmlProcessInfo>(checked((int)count));
                for (var index = 0; index < count; index++)
                {
                    var native = Marshal.PtrToStructure<NvmlProcessInfoNative>(buffer + checked((int)index * rowSize));
                    values.Add(new NvmlProcessInfo(native.ProcessId, native.UsedGpuMemory));
                }

                processes = values;
                return NvmlResult.Success;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        return result;
    }

    private static NvmlResult GetDeviceString(
        DeviceGetStringDelegate getter,
        nint device,
        int length,
        out string value)
    {
        var buffer = new byte[length];
        var result = getter(device, buffer, (uint)buffer.Length);
        value = result == NvmlResult.Success ? Decode(buffer) : string.Empty;
        return result;
    }

    private static NvmlResult GetSystemString(SystemGetStringDelegate getter, int length, out string value)
    {
        var buffer = new byte[length];
        var result = getter(buffer, (uint)buffer.Length);
        value = result == NvmlResult.Success ? Decode(buffer) : string.Empty;
        return result;
    }

    private static string Decode(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return Encoding.UTF8.GetString(buffer, 0, end >= 0 ? end : buffer.Length);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NvmlMemory
    {
        public readonly ulong Total;
        public readonly ulong Free;
        public readonly ulong Used;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NvmlUtilization
    {
        public readonly uint Gpu;
        public readonly uint Memory;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NvmlProcessInfoNative
    {
        public readonly uint ProcessId;
        public readonly ulong UsedGpuMemory;
        public readonly uint GpuInstanceId;
        public readonly uint ComputeInstanceId;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult InitDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult ShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetCountDelegate(out uint count);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetHandleDelegate(uint index, out nint device);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetStringDelegate(nint device, [Out] byte[] value, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult SystemGetStringDelegate([Out] byte[] value, uint length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetMemoryInfoDelegate(nint device, out NvmlMemory memory);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetUtilizationDelegate(nint device, out NvmlUtilization utilization);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetTemperatureDelegate(nint device, uint sensorType, out uint temperature);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetUIntDelegate(nint device, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetIndexedUIntDelegate(nint device, uint index, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetTwoUIntDelegate(nint device, out uint first, out uint second);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceSetUIntDelegate(nint device, uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceSetIndexedUIntDelegate(nint device, uint index, uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceSetIndexDelegate(nint device, uint index);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetClockDelegate(nint device, NvmlClockType clockType, out uint value);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NvmlResult DeviceGetProcessesDelegate(nint device, ref uint count, nint processes);
}
