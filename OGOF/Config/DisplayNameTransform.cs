using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace OGOF;

public sealed class DisplayNameTransform
{
    [JsonPropertyName("match")]
    public string Match { get; set; }

    [JsonPropertyName("replace")]
    public string Replace { get; set; }

    [JsonPropertyName("flags")]
    public string Flags { get; set; }

    [JsonPropertyName("maxMatches"), DefaultValue(1)]
    public int MaxMatches { get; set; } = 1;

    private int _init;

    private Regex? _regex;

    [JsonIgnore]
    [MemberNotNull(nameof(_regex))]
    public Regex Regex
    {
        // ReSharper disable once CognitiveComplexity
        get
        {
            // init lock
#pragma warning disable CS8774 // Member must have a non-null value when exiting.
            if (Interlocked.CompareExchange(ref _init, 1, 0) != 0)
            {
                var rx = _regex;
                while (rx is null)
                {
                    if (!Thread.Yield())
                        Thread.Sleep(0);
                    rx = _regex;
                    if (rx is not null)
                        return rx;
                }
            }
#pragma warning restore CS8774

            // parse flags
            var flags = RegexOptions.Compiled
                        | RegexOptions.IgnoreCase
                        | RegexOptions.CultureInvariant
                        | RegexOptions.ECMAScript;

            if (Flags.Contains('g')) MaxMatches = -1;
            if (Flags.Contains('i')) flags |= RegexOptions.IgnoreCase;
            if (Flags.Contains('m')) flags |= RegexOptions.Multiline;
            if (Flags.Contains('s')) flags |= RegexOptions.Singleline;
            if (Flags.Contains('x')) flags |= RegexOptions.IgnorePatternWhitespace;
            if (Flags.Contains('n')) flags |= RegexOptions.ExplicitCapture;
            if (Flags.Contains('r')) flags |= RegexOptions.RightToLeft;
            return _regex = new(Match, flags);
        }
    }
}