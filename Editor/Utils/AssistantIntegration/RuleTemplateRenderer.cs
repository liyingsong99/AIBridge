using System.Collections.Generic;

namespace AIBridge.Editor
{
    internal static class RuleTemplateRenderer
    {
        public static string Render(string templateBody, IDictionary<string, string> tokens)
        {
            var rendered = templateBody;
            foreach (var pair in tokens)
            {
                rendered = rendered.Replace("{{" + pair.Key + "}}", pair.Value ?? string.Empty);
            }

            return rendered.Trim() + "\n";
        }
    }
}
