//----------------------
// <auto-generated>
//     Generated using the NSwag toolchain v13.20.0.0 (NJsonSchema v10.9.0.0 (Newtonsoft.Json v10.0.0.0)) (http://NSwag.org)
// </auto-generated>
//----------------------

#nullable enable


using StreamVideo.Core.InternalDTO.Responses;
using StreamVideo.Core.InternalDTO.Events;
using StreamVideo.Core.InternalDTO.Models;

namespace StreamVideo.Core.InternalDTO.Requests
{
    using System = global::System;

    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "13.20.0.0 (NJsonSchema v10.9.0.0 (Newtonsoft.Json v10.0.0.0))")]
    internal partial class TranscriptionSettingsRequestInternalDTO
    {
        [Newtonsoft.Json.JsonProperty("closed_caption_mode", Required = Newtonsoft.Json.Required.Default, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [Newtonsoft.Json.JsonConverter(typeof(StreamVideo.Core.Serialization.EnumeratedStructConverter<TranscriptionSettingsRequestClosedCaptionModeInternalEnumDTO>))]
        public TranscriptionSettingsRequestClosedCaptionModeInternalEnumDTO ClosedCaptionMode { get; set; } = default!;

        [Newtonsoft.Json.JsonProperty("language", Required = Newtonsoft.Json.Required.Default, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        [Newtonsoft.Json.JsonConverter(typeof(StreamVideo.Core.Serialization.EnumeratedStructConverter<TranscriptionSettingsRequestLanguageInternalEnumDTO>))]
        public TranscriptionSettingsRequestLanguageInternalEnumDTO Language { get; set; } = default!;

        [Newtonsoft.Json.JsonProperty("mode", Required = Newtonsoft.Json.Required.Default)]
        [Newtonsoft.Json.JsonConverter(typeof(StreamVideo.Core.Serialization.EnumeratedStructConverter<TranscriptionSettingsModeInternalEnumDTO>))]
        public TranscriptionSettingsModeInternalEnumDTO Mode { get; set; } = default!;

    }

}

