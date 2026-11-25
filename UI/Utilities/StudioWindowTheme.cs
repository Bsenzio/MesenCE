using Avalonia.Controls;
using System;
using System.Runtime.InteropServices;

namespace Mesen.Utilities
{
	/// <summary>
	/// Keeps the Windows title bar consistent with the Studio appearance. The content uses
	/// Avalonia theme resources; this small Win32 call only removes a bright system title bar
	/// from the dark experience. It is a no-op on non-Windows platforms and unsupported builds.
	/// </summary>
	internal static class StudioWindowTheme
	{
		private const int DwmwaUseImmersiveDarkMode = 20;
		private const int DwmwaUseImmersiveDarkModeLegacy = 19;

		[DllImport("dwmapi.dll", PreserveSig = true)]
		private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

		public static void Apply(Window window, bool dark)
		{
			if(!OperatingSystem.IsWindows()) {
				return;
			}
			try {
				IntPtr handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
				if(handle == IntPtr.Zero) {
					return;
				}
				int value = dark ? 1 : 0;
				int result = DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
				if(result != 0) {
					DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkModeLegacy, ref value, sizeof(int));
				}
			} catch {
				//A themed title bar is cosmetic; never let it break opening a window.
			}
		}
	}
}
