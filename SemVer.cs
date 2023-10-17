using System.Text.RegularExpressions;

namespace Dev;

public sealed partial class SemVer : IComparable<SemVer>, IEquatable<SemVer>
{
    public readonly int Major;
    public readonly int? Minor, Build, Fix;
    public readonly string? Suffix, BuildVariables;
    public readonly bool IsAny;

    public SemVer(int major, int? minor, int? build, int? fix, string? suffix, string? buildVariables)
    {
        Major = major;
        Minor = minor;
        Build = build;
        Fix = fix;
        Suffix = suffix;
        BuildVariables = buildVariables;
    }

    public SemVer(string src)
    {
        if (string.IsNullOrEmpty(src) || src == "*")
        {
            IsAny = true;
            return;
        }
        var m = VersionRegex().Match(src);
        if (!m.Success)
            throw new FormatException($"Unparsable version string: {src}");
        Major = int.Parse(m.Groups["major"].Value);
        if (m.Groups["minor"].Success)
            Minor = int.Parse(m.Groups["minor"].Value);
        if (m.Groups["build"].Success)
            Build = int.Parse(m.Groups["build"].Value);
        if (m.Groups["fix"].Success)
            Fix = int.Parse(m.Groups["fix"].Value);
        if (m.Groups["suffix"].Success)
            Suffix = m.Groups["suffix"].Value;
        if (m.Groups["buildvars"].Success)
            BuildVariables = m.Groups["buildvars"].Value;
    }

    public bool Equals(SemVer? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Major == other.Major && Minor == other.Minor && Build == other.Build && Fix == other.Fix && string.Equals(Suffix, other.Suffix) && IsAny == other.IsAny;
    }

    public int CompareTo(SemVer? other)
    {
        if (ReferenceEquals(this, other)) return 0;
        if (other is null) return 1;
        var majorComparison = Major.CompareTo(other.Major);
        if (majorComparison != 0) return majorComparison;
        var minorComparison = Nullable.Compare(Minor, other.Minor);
        if (minorComparison != 0) return minorComparison;
        var buildComparison = Nullable.Compare(Build, other.Build);
        if (buildComparison != 0) return buildComparison;
        var fixComparison = Nullable.Compare(Fix, other.Fix);
        if (fixComparison != 0) return fixComparison;
        var suffixComparison = CompareSuffix(Suffix, other.Suffix);
        return suffixComparison != 0 ? suffixComparison : IsAny.CompareTo(other.IsAny);
    }

    private static int CompareSuffix(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
        if (string.IsNullOrEmpty(a)) return 1;
        if (string.IsNullOrEmpty(b)) return -1;
        var aparts = a.Split('.');
        var bparts = b.Split('.');
        for (int i = 0, l = Math.Max(aparts.Length, bparts.Length); i < l; i++)
        {
            if (aparts.Length <= i)
                return -1;
            if (bparts.Length <= i)
                return 1;

            bool ai = int.TryParse(aparts[i], out var aa),
                bi = int.TryParse(bparts[i], out var bb);
            if (ai && bi)
                if (aa != bb)
                    return aa - bb;
                else
                    continue;
            if (ai) return -1;
            if (bi) return 1;
            var sc = string.Compare(aparts[i], bparts[i], StringComparison.Ordinal);
            if (sc != 0)
                return sc;
        }
        return 0;
    }

    public override bool Equals(object? obj)
    {
        if (obj is null) return false;
        if (ReferenceEquals(this, obj)) return true;
        return obj.GetType() == GetType() && Equals((SemVer)obj);
    }

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Build, Fix, Suffix, IsAny);

    private sealed class SemVerEqualityComparer : IEqualityComparer<SemVer>
    {
        public bool Equals(SemVer? x, SemVer? y)
        {
            if (ReferenceEquals(x, y)) return true;
            if (x is null) return false;
            if (y is null) return false;
            if (x.GetType() != y.GetType()) return false;
            return x.Major == y.Major && x.Minor == y.Minor && x.Build == y.Build && x.Fix == y.Fix && string.Equals(x.Suffix, y.Suffix) && x.IsAny == y.IsAny;
        }

        public int GetHashCode(SemVer obj)
        {
            unchecked
            {
                var hashCode = obj.Major;
                hashCode = hashCode * 397 ^ obj.Minor.GetHashCode();
                hashCode = hashCode * 397 ^ obj.Build.GetHashCode();
                hashCode = hashCode * 397 ^ obj.Fix.GetHashCode();
                hashCode = hashCode * 397 ^ (obj.Suffix != null ? obj.Suffix.GetHashCode() : 0);
                hashCode = hashCode * 397 ^ obj.IsAny.GetHashCode();
                return hashCode;
            }
        }
    }

    public static IEqualityComparer<SemVer> SemVerComparer { get; } = new SemVerEqualityComparer();

    public override string ToString()
    {
        if (IsAny)
            return "*";
        var rv = Major.ToString();
        if (Minor.HasValue)
        {
            rv += "." + Minor;
            if (Build.HasValue)
            {
                rv += "." + Build;
                if (Fix.HasValue)
                    rv += "." + Fix;
            }
        }
        if (!string.IsNullOrEmpty(Suffix))
            rv += "-" + Suffix;
        if (!string.IsNullOrEmpty(BuildVariables))
            rv += "+" + BuildVariables;
        return rv;
    }

    [GeneratedRegex("^(?<major>[0-9]+)(\\.(?<minor>[0-9]+))?(\\.(?<build>[0-9]+))?(\\.(?<fix>[0-9]+))?(-(?<suffix>.*))?(\\+(?<buildvars>.*))?$", RegexOptions.IgnoreCase | RegexOptions.Compiled, "en-BE")]
    private static partial Regex VersionRegex();
}