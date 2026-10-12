using System;
using System.Collections;
using System.Linq;

namespace TelegArm.SelfTest
{
    /// <summary>A failed check: its message is the whole report, so no stack trace is printed for it.</summary>
    internal sealed class CheckFailed : Exception
    {
        public CheckFailed(string message) : base(message) { }
    }

    internal static class Check
    {
        public static void True(bool condition, string what)
        {
            if (!condition) throw new CheckFailed(what);
        }

        /// <summary>Equality that treats two sequences (a List&lt;string&gt;) as equal when their items are.</summary>
        public static void Equal(object expected, object actual, string what)
        {
            if (!Same(expected, actual))
                throw new CheckFailed(what + ": expected " + Show(expected) + ", got " + Show(actual));
        }

        internal static bool Same(object a, object b)
        {
            if (a is IEnumerable ea && !(a is string) && b is IEnumerable eb && !(b is string))
                return ea.Cast<object>().SequenceEqual(eb.Cast<object>());
            return Equals(a, b);
        }

        private static string Show(object o)
        {
            if (o == null) return "null";
            if (o is IEnumerable e && !(o is string)) return "[" + string.Join(", ", e.Cast<object>()) + "]";
            return "\"" + o + "\"";
        }
    }
}
