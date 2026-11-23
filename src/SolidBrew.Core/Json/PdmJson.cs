using System;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace SolidBrew.Core.Json
{
    /// <summary>
    /// Единые настройки JSON для сервера и клиента: camelCase, перечисления строками camelCase,
    /// null не пишется, чтение без учёта регистра, кириллица пишется как есть (без \uXXXX).
    /// Ключи словарей (имена конфигураций и свойств) не преобразуются.
    /// </summary>
    public static class PdmJson
    {
        private static readonly Lazy<JsonSerializerOptions> LazyOptions = new Lazy<JsonSerializerOptions>(() =>
        {
            var options = new JsonSerializerOptions();
            Apply(options);
            options.MakeReadOnly(populateMissingResolver: true);
            return options;
        });

        /// <summary>Готовые неизменяемые настройки.</summary>
        public static JsonSerializerOptions Options => LazyOptions.Value;

        /// <summary>
        /// Применить настройки PDM к существующему объекту (например, к настройкам JSON в ASP.NET Core).
        /// </summary>
        public static void Apply(JsonSerializerOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            options.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
            options.DictionaryKeyPolicy = null;
            options.PropertyNameCaseInsensitive = true;
            options.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
            options.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
            options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }
    }
}
