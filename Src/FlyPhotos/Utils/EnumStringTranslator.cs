using System;
using System.Collections.Generic;
using System.Linq;

namespace FlyPhotos.Utils;

public class EnumStringTranslator<TEnum>(Dictionary<TEnum, string> map) where TEnum : struct, Enum
{
    private readonly Dictionary<TEnum, string> _enumToString = new(map);
    private readonly Dictionary<string, TEnum> _stringToEnum = map.ToDictionary(
        kvp => kvp.Value,
        kvp => kvp.Key,
        StringComparer.OrdinalIgnoreCase);

    public TEnum ToEnum(string name)
    {
        return string.IsNullOrWhiteSpace(name) ? default : _stringToEnum.GetValueOrDefault(name);
    }

    public string ToString(TEnum value)
    {
        return _enumToString.GetValueOrDefault(value);
    }
}