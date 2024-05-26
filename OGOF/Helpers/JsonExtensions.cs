using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace OGOF;

public static class JsonExtensions
{
    [return: NotNullIfNotNull(nameof(@default))]
    public static T Get<T>(this JsonElement e, string propName, in T @default = default!)
        where T : IConvertible?
    {
        if (!e.TryGetProperty(propName, out var prop))
            return default!;

        if (typeof(T) == typeof(string))
            return (T) (object) prop.GetString()!;


        if (typeof(T) == typeof(bool))
        {
            var v = prop.GetBoolean();
            return Unsafe.As<bool, T>(ref v);
        }

        if (typeof(T) == typeof(sbyte))
        {
            var v = prop.GetSByte();
            return Unsafe.As<sbyte, T>(ref v);
        }

        if (typeof(T) == typeof(byte))
        {
            var v = prop.GetByte();
            return Unsafe.As<byte, T>(ref v);
        }

        if (typeof(T) == typeof(short))
        {
            var v = prop.GetInt16();
            return Unsafe.As<short, T>(ref v);
        }

        if (typeof(T) == typeof(ushort))
        {
            var v = prop.GetUInt16();
            return Unsafe.As<ushort, T>(ref v);
        }

        if (typeof(T) == typeof(int))
        {
            var v = prop.GetInt32();
            return Unsafe.As<int, T>(ref v);
        }

        if (typeof(T) == typeof(uint))
        {
            var v = prop.GetUInt32();
            return Unsafe.As<uint, T>(ref v);
        }

        if (typeof(T) == typeof(long))
        {
            var v = prop.GetInt64();
            return Unsafe.As<long, T>(ref v);
        }

        if (typeof(T) == typeof(ulong))
        {
            var v = prop.GetUInt64();
            return Unsafe.As<ulong, T>(ref v);
        }

        if (typeof(T) == typeof(float))
        {
            var v = prop.GetSingle();
            return Unsafe.As<float, T>(ref v);
        }

        if (typeof(T) == typeof(double))
        {
            var v = prop.GetDouble();
            return Unsafe.As<double, T>(ref v);
        }

        return @default;
    }
}