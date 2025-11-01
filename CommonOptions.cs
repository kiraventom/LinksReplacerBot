using System.Text.Json;

namespace LinksReplacerBot;

public static class CommonOptions
{
   public static JsonSerializerOptions Json = new()
   {
      WriteIndented = true, AllowTrailingCommas = true
   };
}
