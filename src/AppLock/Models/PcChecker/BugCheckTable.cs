using System.Collections.Generic;
using System.Globalization;

namespace AppLock.Models.PcChecker;

/// <summary>Turns blue-screen bug check codes into their documented names.</summary>
public static class BugCheckTable
{
    private static readonly Dictionary<uint, string> Names = new()
    {
        [0x0000000A] = "IRQL_NOT_LESS_OR_EQUAL",
        [0x0000001A] = "MEMORY_MANAGEMENT",
        [0x0000001E] = "KMODE_EXCEPTION_NOT_HANDLED",
        [0x00000024] = "NTFS_FILE_SYSTEM",
        [0x0000002E] = "DATA_BUS_ERROR",
        [0x0000003B] = "SYSTEM_SERVICE_EXCEPTION",
        [0x00000050] = "PAGE_FAULT_IN_NONPAGED_AREA",
        [0x0000007A] = "KERNEL_DATA_INPAGE_ERROR",
        [0x0000007B] = "INACCESSIBLE_BOOT_DEVICE",
        [0x0000007E] = "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED",
        [0x0000008E] = "KERNEL_MODE_EXCEPTION_NOT_HANDLED",
        [0x0000009C] = "MACHINE_CHECK_EXCEPTION",
        [0x0000009F] = "DRIVER_POWER_STATE_FAILURE",
        [0x000000A0] = "INTERNAL_POWER_ERROR",
        [0x000000BE] = "ATTEMPTED_WRITE_TO_READONLY_MEMORY",
        [0x000000C2] = "BAD_POOL_CALLER",
        [0x000000C4] = "DRIVER_VERIFIER_DETECTED_VIOLATION",
        [0x000000C5] = "DRIVER_CORRUPTED_EXPOOL",
        [0x000000D1] = "DRIVER_IRQL_NOT_LESS_OR_EQUAL",
        [0x000000DA] = "SYSTEM_PTE_MISUSE",
        [0x000000EA] = "THREAD_STUCK_IN_DEVICE_DRIVER",
        [0x000000ED] = "UNMOUNTABLE_BOOT_VOLUME",
        [0x000000EF] = "CRITICAL_PROCESS_DIED",
        [0x000000F4] = "CRITICAL_OBJECT_TERMINATION",
        [0x000000F7] = "DRIVER_OVERRAN_STACK_BUFFER",
        [0x000000FC] = "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY",
        [0x00000101] = "CLOCK_WATCHDOG_TIMEOUT",
        [0x00000109] = "CRITICAL_STRUCTURE_CORRUPTION",
        [0x0000010E] = "VIDEO_MEMORY_MANAGEMENT_INTERNAL",
        [0x00000116] = "VIDEO_TDR_ERROR",
        [0x00000117] = "VIDEO_TDR_TIMEOUT_DETECTED",
        [0x00000124] = "WHEA_UNCORRECTABLE_ERROR",
        [0x00000133] = "DPC_WATCHDOG_VIOLATION",
        [0x00000139] = "KERNEL_SECURITY_CHECK_FAILURE",
        [0x0000013A] = "KERNEL_MODE_HEAP_CORRUPTION",
        [0x00000141] = "VIDEO_ENGINE_TIMEOUT_DETECTED",
        [0x00000144] = "BUGCODE_USB3_DRIVER",
        [0x0000014C] = "FATAL_ABNORMAL_RESET_ERROR",
        [0x00000154] = "UNEXPECTED_STORE_EXCEPTION",
        [0x0000015E] = "BUGCODE_NDIS_DRIVER_LIVE_DUMP",
        [0x000001C8] = "ASSERT_FAILED",
        [0x000001D1] = "BUGCODE_POWER_FAILURE",
        [0x000002C1] = "PAGE_FAULT_IN_NONPAGED_AREA_SECONDARY",
        [0x0000034B] = "CACHE_MANAGER_CORRUPTION",
        [0x00000BFE] = "BC_BLUETOOTH_VERIFIER_FAULT",
        [0x00000C1D] = "BC_BLUETOOTH_HCI_VHCI_FAULT",
        [0x00000C2A] = "BC_BLUETOOTH_SCO_VERIFIER_FAULT",
        [0x00000C4E] = "BC_BLUETOOTH_L2CAP_VERIFIER_FAULT",
        [0x00000C5A] = "HAL_INITIALIZATION_FAILED"
    };

    public static bool TryGetName(uint code, out string name) => Names.TryGetValue(code, out name!);

    /// <summary>Formatted like Windows does, e.g. "0x0000009F DRIVER_POWER_STATE_FAILURE".</summary>
    public static string Format(uint code)
        => TryGetName(code, out var name)
            ? $"0x{code:X8} {name}"
            : $"0x{code:X8}";

    public static string Format(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "unknown code";
        var trimmed = code.Trim();
        var digits = trimmed.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase) ? trimmed[2..] : trimmed;
        return uint.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed)
            ? Format(parsed)
            : trimmed;
    }
}
