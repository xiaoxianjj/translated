using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Translated
{
    internal sealed class InputDevices
    {
        [StructLayout(LayoutKind.Sequential)] struct Device { public IntPtr Handle; public uint Type; }
        [DllImport("user32.dll")] static extern uint GetRawInputDeviceList([Out] Device[] devices, ref uint count, uint size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder text, ref uint size);
        internal string Keyboards = "通用键盘", Mice = "通用鼠标";
        internal static InputDevices Read()
        {
            var result = new InputDevices();
            try
            {
                uint count = 0, size = (uint)Marshal.SizeOf<Device>();
                if (GetRawInputDeviceList(null, ref count, size) == uint.MaxValue || count == 0 || count > 1024) return result;
                var devices = new Device[count];
                uint actual = GetRawInputDeviceList(devices, ref count, size);
                if (actual == uint.MaxValue) return result;
                var keyboards = new List<string>(); var mice = new List<string>();
                foreach (var device in devices.Take((int)actual).Where(d => d.Type == 0 || d.Type == 1))
                {
                    uint length = 0;
                    GetRawInputDeviceInfo(device.Handle, 0x20000007, null, ref length);
                    if (length == 0 || length > 4096) continue;
                    var path = new StringBuilder((int)length);
                    if (GetRawInputDeviceInfo(device.Handle, 0x20000007, path, ref length) == uint.MaxValue) continue;
                    string name = FriendlyName(path.ToString()) ?? (device.Type == 1 ? "HID 键盘" : "HID 鼠标");
                    (device.Type == 1 ? keyboards : mice).Add(name);
                }
                string Label(List<string> names, string fallback) => names.Count == 0 ? fallback : string.Join(" · ", names.Distinct().Take(2));
                result.Keyboards = Label(keyboards, result.Keyboards);
                result.Mice = Label(mice, result.Mice);
            }
            catch (Exception e) when (e is UnauthorizedAccessException || e is System.Security.SecurityException ||
                e is System.IO.IOException || e is ArgumentException) { /* Generic diagrams remain usable. */ }
            return result;
        }
        static string FriendlyName(string path)
        {
            var parts = path.TrimStart('\\', '?').Split('#');
            if (parts.Length < 3) return null;
            using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + parts[0] + "\\" + parts[1] + "\\" + parts[2]))
            {
                var name = key?.GetValue("FriendlyName") as string ?? key?.GetValue("DeviceDesc") as string;
                return string.IsNullOrWhiteSpace(name) ? null : name.Split(';').Last();
            }
        }
    }
}
