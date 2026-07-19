using System.Text.RegularExpressions;

namespace Srm.PolicyEngine;

public class EnvExpander
{
    private static readonly Regex VarPattern = new(@"%([^%]+)%", RegexOptions.Compiled);

    public string Expand(string input, Dictionary<string, string>? userVars = null)
    {
        if (string.IsNullOrEmpty(input)) return input;

        return VarPattern.Replace(input, match =>
        {
            var key = match.Groups[1].Value;

            if (userVars != null && userVars.TryGetValue(key, out var userVal))
                return Expand(userVal, userVars);

            var sysVal = Environment.GetEnvironmentVariable(key);
            if (sysVal != null) return sysVal;

            return match.Value;
        });
    }
}
