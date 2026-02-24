using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;

namespace FlyPhotos.Infra.Configuration;

internal partial class WindowStatePersistence : IDictionary<string, object>
{
    private readonly Dictionary<string, object> _data = [];
    private readonly Action<string> _externalSaveAction;

    /// <summary>
    /// Initializes the dictionary from a JSON string.
    /// </summary>
    /// <param name="jsonString">The JSON string to load from.</param>
    /// <param name="externalSaveAction">A callback fired whenever the data changes, providing the updated JSON string.</param>
    public WindowStatePersistence(string jsonString, Action<string> externalSaveAction = null)
    {
        _externalSaveAction = externalSaveAction;
        if (string.IsNullOrWhiteSpace(jsonString)) return;
        try
        {
            if (JsonNode.Parse(jsonString) is not JsonObject jo) return;
            foreach (var node in jo)
            {
                if (node.Value is JsonValue jvalue && jvalue.TryGetValue<string>(out var value))
                    _data[node.Key] = value;
            }
        }
        catch
        {
            // Ignore parse errors, will just start with an empty dictionary
        }
    }

    /// <summary>
    /// Converts the current dictionary state to a JSON string.
    /// </summary>
    public string ToJsonString()
    {
        JsonObject jo = [];
        foreach (var item in _data)
        {
            if (item.Value is string s) // In this case we only need string support.
                jo.Add(item.Key, s);
        }
        return jo.ToJsonString();
    }

    /// <summary>
    /// Triggers the external save callback with the latest JSON.
    /// </summary>
    private void Save()
    {
        _externalSaveAction?.Invoke(ToJsonString());
    }

    public object this[string key] { get => _data[key]; set { _data[key] = value; Save(); } }

    public ICollection<string> Keys => _data.Keys;

    public ICollection<object> Values => _data.Values;

    public int Count => _data.Count;

    public bool IsReadOnly => false;

    public void Add(string key, object value)
    {
        _data.Add(key, value); Save();
    }

    public void Add(KeyValuePair<string, object> item)
    {
        ((IDictionary<string, object>)_data).Add(item); Save();
    }

    public void Clear()
    {
        _data.Clear(); Save();
    }

    public bool Remove(string key)
    {
        var removed = _data.Remove(key);
        if (removed) Save();
        return removed;
    }

    public bool Remove(KeyValuePair<string, object> item)
    {
        var removed = ((IDictionary<string, object>)_data).Remove(item);
        if (removed) Save();
        return removed;
    }

    public bool Contains(KeyValuePair<string, object> item) => ((IDictionary<string, object>)_data).Contains(item);

    public bool ContainsKey(string key) => _data.ContainsKey(key);

    public void CopyTo(KeyValuePair<string, object>[] array, int arrayIndex) => ((IDictionary<string, object>)_data).CopyTo(array, arrayIndex);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out object value) => _data.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, object>> GetEnumerator() => _data.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _data.GetEnumerator();
}