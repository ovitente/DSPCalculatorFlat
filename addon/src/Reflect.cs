// Reflect.cs - small reflection helpers for reading DSPCalculator objects by member name, so a renamed or
// removed member turns into a clear message instead of a MissingFieldException at JIT time.
using System;
using System.Reflection;
using HarmonyLib;

namespace DSPCalculatorFlat
{
    internal class MemberMissingException : Exception
    {
        public MemberMissingException(string msg) : base(msg) { }
    }

    internal static class Reflect
    {
        /// Value of an instance field or property (public or not). Throws MemberMissingException when absent.
        public static object Get(object o, string name)
        {
            if (o == null) throw new MemberMissingException("cannot read '" + name + "' of a null object");
            Type t = o.GetType();
            FieldInfo f = AccessTools.Field(t, name);
            if (f != null) return f.GetValue(o);
            PropertyInfo p = AccessTools.Property(t, name);
            if (p != null) return p.GetValue(o, null);
            throw new MemberMissingException(t.FullName + "." + name + " not found (DSPCalculator changed?)");
        }

        /// Like Get, but returns null when the member does not exist.
        public static object TryGet(object o, string name)
        {
            if (o == null) return null;
            Type t = o.GetType();
            FieldInfo f = AccessTools.Field(t, name);
            if (f != null) return f.GetValue(o);
            PropertyInfo p = AccessTools.Property(t, name);
            if (p != null) return p.GetValue(o, null);
            return null;
        }

        public static int GetInt(object o, string name) { return Convert.ToInt32(Get(o, name)); }
        public static double GetDouble(object o, string name) { return Convert.ToDouble(Get(o, name)); }
        public static bool GetBool(object o, string name) { return Convert.ToBoolean(Get(o, name)); }

        /// Calls an instance method by name and argument count. args may contain out-parameter slots.
        public static object Call(object o, string name, params object[] args)
        {
            if (o == null) throw new MemberMissingException("cannot call '" + name + "' on a null object");
            Type t = o.GetType();
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == name && m.GetParameters().Length == args.Length)
                    return m.Invoke(o, args);
            throw new MemberMissingException(t.FullName + "." + name + "(" + args.Length + " args) not found (DSPCalculator changed?)");
        }

        public static bool HasMethod(object o, string name, int argCount)
        {
            if (o == null) return false;
            foreach (MethodInfo m in o.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == name && m.GetParameters().Length == argCount) return true;
            return false;
        }

        /// Static field of a type found by full name. Returns false when the type or field is missing.
        public static bool SetStatic(string typeName, string field, object value)
        {
            Type t = AccessTools.TypeByName(typeName);
            if (t == null) return false;
            FieldInfo f = AccessTools.Field(t, field);
            if (f == null || !f.IsStatic) return false;
            f.SetValue(null, value);
            return true;
        }
    }
}
