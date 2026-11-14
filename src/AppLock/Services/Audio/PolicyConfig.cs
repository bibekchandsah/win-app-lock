using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AppLock.Services.Audio
{
    /// <summary>
    /// Switches the Windows default audio endpoint through the (undocumented but stable)
    /// PolicyConfig COM interface — the same call Sound settings uses for "Set as default".
    /// </summary>
    internal static class PolicyConfig
    {
        public const int RoleConsole = 0;
        public const int RoleMultimedia = 1;
        public const int RoleCommunications = 2;

        /// <summary>
        /// Makes <paramref name="deviceId"/> the default for the given roles.
        /// Returns true when at least one role was applied.
        /// </summary>
        public static bool SetDefaultEndpoint(string deviceId, params int[] roles)
        {
            if (string.IsNullOrEmpty(deviceId)) return false;

            try
            {
                var client = (IPolicyConfig)new CPolicyConfigClient();
                try
                {
                    bool anyApplied = false;
                    foreach (int role in roles)
                    {
                        try
                        {
                            int hr = client.SetDefaultEndpoint(deviceId, role);
                            if (hr == 0) anyApplied = true;
                            else Debug.WriteLine($"[PolicyConfig] SetDefaultEndpoint role {role} -> 0x{hr:X8}");
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[PolicyConfig] SetDefaultEndpoint role {role} failed: {ex.Message}");
                        }
                    }
                    return anyApplied;
                }
                finally
                {
                    Marshal.ReleaseComObject(client);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PolicyConfig] Set default endpoint error: {ex.Message}");
                return false;
            }
        }

        [ComImport]
        [Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
        private class CPolicyConfigClient
        {
        }

        [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IPolicyConfig
        {
            int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
            int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr ppFormat);
            int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
            int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
            int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
            int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
            int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
            int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr key, IntPtr pv);
            int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr key, IntPtr pv);
            int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, int role);
            int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
        }
    }
}
