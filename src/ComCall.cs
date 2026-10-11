// Late-bound calls into the TwinCAT Automation Interface.
// Many functions live on versioned interfaces (ITcPlcIECProject2..7, ITcSysManager3..15 ...). Which of them exist depends
// on the TwinCAT build the interop was generated from. Calling them through reflection keeps TcAgent compilable with any
// build and gives a clear message ("needs a newer TwinCAT") instead of a compile error.
//
// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Text.RegularExpressions;
using TCatSysManagerLib;

namespace TcAgent;

public static class ComCall
{
    static List<Type> interfaces;

    /// <summary>All interfaces of the interop, highest version first within a family (ITcSysManager15 before ITcSysManager3).</summary>
    static List<Type> Interfaces => interfaces ??= typeof(ITcSysManager).Assembly.GetTypes()
        .Where(t => t.IsInterface)
        .OrderBy(t => Regex.Replace(t.Name, @"\d+$", ""))
        .ThenByDescending(t => int.TryParse(Regex.Match(t.Name, @"\d+$").Value, out var n) ? n : 1)
        .ToList();

    /// <summary>true if the object offers the method on any interface.</summary>
    public static bool Has(object target, string method) =>
        Interfaces.Any(t => t.GetMethods().Any(m => m.Name == method) && t.IsInstanceOfType(target));

    /// <summary>Calls a method by name. Missing trailing arguments are filled with their default value (optional COM parameters).</summary>
    public static object Invoke(object target, string method, params object[] args)
    {
        foreach (var t in Interfaces)
        {
            var candidates = t.GetMethods().Where(m => m.Name == method && m.GetParameters().Length >= args.Length)
                                           .OrderBy(m => m.GetParameters().Length).ToList();
            if (candidates.Count == 0 || !t.IsInstanceOfType(target)) continue;
            var m = candidates[0];
            var ps = m.GetParameters();
            var full = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++)
                full[i] = i < args.Length ? Convert(args[i], ps[i].ParameterType)
                        : ps[i].HasDefaultValue ? ps[i].DefaultValue
                        : ps[i].IsOptional ? Type.Missing
                        : throw new Exception($"{method}: parameter '{ps[i].Name}' is required");
            try { return m.Invoke(target, full); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }
        throw new NotSupportedException($"{method} is not available - it needs a newer TwinCAT version than the one TcAgent's interop was generated from (or the object does not support it).");
    }

    static object Convert(object v, Type t)
    {
        if (v == null || t.IsInstanceOfType(v)) return v;
        var target = t.IsByRef ? t.GetElementType() : t;
        if (target.IsEnum) return Enum.ToObject(target, v);
        if (v is IConvertible && typeof(IConvertible).IsAssignableFrom(target))
            return System.Convert.ChangeType(v, target, System.Globalization.CultureInfo.InvariantCulture);
        return v;
    }

    public static object Get(object target, string property)
    {
        foreach (var t in Interfaces)
        {
            var p = t.GetProperty(property);
            if (p == null || !p.CanRead || !t.IsInstanceOfType(target)) continue;
            try { return p.GetValue(target); }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }
        throw new NotSupportedException($"Property {property} is not available in this TwinCAT version.");
    }

    public static void Set(object target, string property, object value)
    {
        foreach (var t in Interfaces)
        {
            var p = t.GetProperty(property);
            if (p == null || !p.CanWrite || !t.IsInstanceOfType(target)) continue;
            try { p.SetValue(target, value); return; }
            catch (TargetInvocationException ex) when (ex.InnerException != null) { throw ex.InnerException; }
        }
        throw new NotSupportedException($"Property {property} cannot be set in this TwinCAT version.");
    }
}
