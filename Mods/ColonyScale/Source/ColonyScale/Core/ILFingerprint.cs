using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;

namespace ColonyScale
{
    /// <summary>
    /// Version-stable fingerprint of a method's original IL plus its signature and local variables. Metadata tokens
    /// are resolved to names and type names are written without assembly versions, so an unchanged method hashes the
    /// same across game builds and across runtimes (Mono in game, .NET in the offline pipeline). Nothing here may use
    /// a runtime's own ToString formatting: floats are written as IEEE-754 bit patterns, strings are escaped to ASCII,
    /// and an operand kind without an explicit canonical form throws instead of being formatted.
    /// Must stay free of Verse/Unity calls: the offline pipeline runs it outside the game.
    /// </summary>
    public static class ILFingerprint
    {
        public static string Compute(MethodBase method) => Hash(Canonical(method));

        public static string Hash(string canonical)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                var sb = new StringBuilder(32);
                for (int i = 0; i < 16; i++) sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString();
            }
        }

        public static string Canonical(MethodBase method)
        {
            var instructions = PatchProcessor.GetOriginalInstructions(method);
            var labelIndex = new Dictionary<Label, int>();
            for (int i = 0; i < instructions.Count; i++)
                foreach (var label in instructions[i].labels)
                    labelIndex[label] = i;

            var sb = new StringBuilder();
            sb.Append(Signature(method)).Append('\n');
            sb.Append(Locals(method)).Append('\n');
            for (int i = 0; i < instructions.Count; i++)
            {
                var ins = instructions[i];
                foreach (var block in ins.blocks)
                    sb.Append('[').Append(block.blockType).Append(block.catchType != null ? ":" + TypeName(block.catchType) : "").Append(']');
                sb.Append(ins.opcode.Name);
                if (ins.operand != null) sb.Append(' ').Append(Operand(ins.operand, labelIndex));
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The full ordered local signature: InitLocals, then every slot's type and pinning.</summary>
        public static string Locals(MethodBase method)
        {
            var body = method.GetMethodBody();
            if (body == null) return "locals none";
            var sb = new StringBuilder("locals ").Append(body.InitLocals ? "init" : "noinit");
            foreach (var local in body.LocalVariables.OrderBy(l => l.LocalIndex))
                sb.Append(' ').Append(Local(local));
            return sb.ToString();
        }

        private static string Local(LocalVariableInfo local) =>
            "loc" + local.LocalIndex.ToString(CultureInfo.InvariantCulture) + ":" + TypeName(local.LocalType) + (local.IsPinned ? " pinned" : "");

        internal static string Operand(object operand, Dictionary<Label, int> labelIndex)
        {
            switch (operand)
            {
                case Label label:
                    return "L" + LabelIndex(label, labelIndex);
                case Label[] labels:
                    return string.Join(",", labels.Select(l => "L" + LabelIndex(l, labelIndex)));
                case MethodBase m:
                    return Signature(m);
                case FieldInfo f:
                    return TypeName(f.DeclaringType) + "::" + f.Name + ":" + TypeName(f.FieldType);
                case Type t:
                    return TypeName(t);
                case LocalVariableInfo lv: // LocalBuilder derives from it
                    return Local(lv);
                case ParameterInfo pi:
                    return "arg" + pi.Position.ToString(CultureInfo.InvariantCulture) + ":" + TypeName(pi.ParameterType);
                case string s:
                    return EscapeString(s);
                case float fl:
                    return "f32:" + BitConverter.ToInt32(BitConverter.GetBytes(fl), 0).ToString("x8", CultureInfo.InvariantCulture);
                case double d:
                    return "f64:" + BitConverter.DoubleToInt64Bits(d).ToString("x16", CultureInfo.InvariantCulture);
                case sbyte v: return v.ToString(CultureInfo.InvariantCulture);
                case byte v: return v.ToString(CultureInfo.InvariantCulture);
                case short v: return v.ToString(CultureInfo.InvariantCulture);
                case ushort v: return v.ToString(CultureInfo.InvariantCulture);
                case int v: return v.ToString(CultureInfo.InvariantCulture);
                case uint v: return v.ToString(CultureInfo.InvariantCulture);
                case long v: return v.ToString(CultureInfo.InvariantCulture);
                case ulong v: return v.ToString(CultureInfo.InvariantCulture);
                default:
                    throw new NotSupportedException("no canonical form for IL operand of type " + operand.GetType().FullName);
            }
        }

        private static string LabelIndex(Label label, Dictionary<Label, int> labelIndex) =>
            (labelIndex.TryGetValue(label, out var idx) ? idx : -1).ToString(CultureInfo.InvariantCulture);

        /// <summary>Quoted, with everything outside printable ASCII as \uXXXX, so the bytes hashed never depend on an encoder.</summary>
        private static string EscapeString(string s)
        {
            var sb = new StringBuilder(s.Length + 2).Append('"');
            foreach (char c in s)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c >= 0x20 && c < 0x7f) sb.Append(c);
                else sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Calling convention, return type and <see cref="MemberId"/>.</summary>
        public static string Signature(MethodBase m)
        {
            var sb = new StringBuilder(m.IsStatic ? "static " : "instance ");
            if ((m.CallingConvention & CallingConventions.VarArgs) != 0) sb.Append("vararg ");
            if ((m.CallingConvention & CallingConventions.ExplicitThis) != 0) sb.Append("explicit ");
            sb.Append(m is MethodInfo mi ? TypeName(mi.ReturnType) : "System.Void").Append(' ');
            return sb.Append(MemberId(m)).ToString();
        }

        /// <summary>Type-qualified method name with parameter types, without assembly versions. Key of the fingerprint store.</summary>
        public static string MemberId(MethodBase m)
        {
            var sb = new StringBuilder();
            sb.Append(TypeName(m.DeclaringType)).Append("::").Append(m.Name);
            if (m.IsGenericMethod)
                sb.Append('<').Append(string.Join(",", m.GetGenericArguments().Select(TypeName))).Append('>');
            sb.Append('(').Append(string.Join(",", m.GetParameters().Select(p => TypeName(p.ParameterType)))).Append(')');
            return sb.ToString();
        }

        public static string TypeName(Type t)
        {
            if (t == null) return "?";
            if (t.IsGenericParameter) return (t.DeclaringMethod != null ? "!!" : "!") + t.Name;
            if (t.IsByRef) return TypeName(t.GetElementType()) + "&";
            if (t.IsPointer) return TypeName(t.GetElementType()) + "*";
            if (t.IsArray) return TypeName(t.GetElementType()) + "[" + new string(',', t.GetArrayRank() - 1) + "]";

            string name = t.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0) name = name.Substring(0, tick);
            // The declaring chain is written by definition (metadata names, arity kept), not by whatever constructed
            // or open form a runtime's DeclaringType returns; a nested type carries all generic arguments itself.
            string prefix = t.IsNested ? DefinitionName(t.DeclaringType) + "+" : (string.IsNullOrEmpty(t.Namespace) ? "" : t.Namespace + ".");
            if (!t.IsGenericType) return prefix + name;
            return prefix + name + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
        }

        private static string DefinitionName(Type t)
        {
            if (t.IsGenericType && !t.IsGenericTypeDefinition) t = t.GetGenericTypeDefinition();
            if (t.IsNested) return DefinitionName(t.DeclaringType) + "+" + t.Name;
            return (string.IsNullOrEmpty(t.Namespace) ? "" : t.Namespace + ".") + t.Name;
        }
    }
}
