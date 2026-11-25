using System.Runtime.InteropServices;
using Windows.Devices.Sensors;

namespace SurfaceFrame;

internal sealed class DisplayBrightness
{
    internal (int Target, string Status) Update(DisplayBrightnessMode mode, int manual, int? previousTarget)
    {
        double? lux = mode == DisplayBrightnessMode.Adaptive ? ReadAmbientLight() : null;
        int target = lux is double light ? AdaptiveTarget(light, previousTarget) : manual;
        int? applied = SetDisplayBrightness(target, correctDrift: lux.HasValue);
        if (applied is null)
            return (target, "Windows non espone un display con luminosità regolabile.");
        if (lux is double measured)
            return (target, $"Adattiva · {measured:0.#} lux · {applied}%.");
        if (mode == DisplayBrightnessMode.Adaptive)
            return (target, $"Sensore di luce non disponibile. Livello di riserva {applied}%.");
        return (target, $"Luminosità manuale {applied}%.");
    }

    internal static int AdaptiveTarget(double lux, int? previousTarget)
    {
        int target = (int)Math.Round(8 + 87 * Math.Log10(Math.Clamp(lux, 0, 2000) + 1) / Math.Log10(2001));
        if (previousTarget is not int previous)
            return target;
        int change = target - previous;
        if (Math.Abs(change) < 4)
            return previous;
        return previous + Math.Clamp(change, -12, 12);
    }

    private static double? ReadAmbientLight()
    {
        try
        {
            double? reading = LightSensor.GetDefault()?.GetCurrentReading()?.IlluminanceInLux;
            return reading is double value && double.IsFinite(value) && value >= 0 ? value : null;
        }
        catch (Exception error) when (error is COMException or UnauthorizedAccessException or
            PlatformNotSupportedException or TypeLoadException)
        {
            return null;
        }
    }

    private static int? SetDisplayBrightness(int target, bool correctDrift)
    {
        Type? locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator");
        if (locatorType is null)
            return null;

        dynamic? locator = null;
        dynamic? services = null;
        dynamic? monitors = null;
        try
        {
            locator = Activator.CreateInstance(locatorType);
            if (locator is null)
                return null;
            services = locator.ConnectServer(".", @"root\WMI");
            monitors = services!.ExecQuery("SELECT * FROM WmiMonitorBrightness WHERE Active = TRUE", "WQL", 48);
            foreach (object entry in (System.Collections.IEnumerable)monitors!)
            {
                try
                {
                    dynamic monitor = entry;
                    int current = Convert.ToInt32(monitor.Properties_.Item("CurrentBrightness").Value);
                    string instanceName = Convert.ToString(monitor.Properties_.Item("InstanceName").Value)!;
                    if (string.IsNullOrEmpty(instanceName))
                        continue;
                    int best = target;
                    if (monitor.Properties_.Item("Level").Value is Array levels && levels.Length > 0)
                        best = levels.Cast<object>().Select(value => Convert.ToInt32(value))
                            .MinBy(level => Math.Abs(level - target));
                    if (current == best || correctDrift && Math.Abs(current - best) < 3)
                        return current;
                    return WriteBrightness(services, instanceName, best) ? best : null;
                }
                finally { Release(entry); }
            }
            return null;
        }
        catch (COMException error) when (error.HResult == unchecked((int)0x80041010) ||
            error.HResult == unchecked((int)0x8004100E))
        {
            return null;
        }
        finally
        {
            Release(monitors);
            Release(services);
            Release(locator);
        }
    }

    private static bool WriteBrightness(object connection, string instanceName, int brightness)
    {
        dynamic services = connection;
        // Escape the display driver’s instance name as a WMI object-path key.
        string key = instanceName.Replace("\\", "\\\\").Replace("\"", "\\\"");
        dynamic? method = null;
        dynamic? input = null;
        dynamic? result = null;
        try
        {
            method = services.Get("WmiMonitorBrightnessMethods.InstanceName=\"" + key + "\"");
            input = method!.Methods_.Item("WmiSetBrightness").InParameters.SpawnInstance_();
            input!.Properties_.Item("Timeout").Value = 0u;
            input.Properties_.Item("Brightness").Value = (byte)brightness;
            result = method.ExecMethod_("WmiSetBrightness", input);
            return result is not null && Convert.ToUInt32(result.Properties_.Item("ReturnValue").Value) == 0;
        }
        finally
        {
            Release(result);
            Release(input);
            Release(method);
        }
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }
}
