namespace OGOF;

[RegisterTypeInIl2Cpp]
public class MetadataComponent : MonoBehaviour
{
    public Dictionary<string, object> Metadata { get; } = new();

    public object? this[string key]
    {
        get => Metadata.GetValueOrDefault(key);
        set
        {
            if (value == null)
                Metadata.Remove(key);
            else
                Metadata[key] = value;
        }
    }

    public bool TryGetValue(string key, out string? value)
        => (value = (string?) Metadata.GetValueOrDefault(key)) is not null;
}