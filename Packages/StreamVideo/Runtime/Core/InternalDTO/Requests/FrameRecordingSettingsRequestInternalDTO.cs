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
    internal partial class FrameRecordingSettingsRequestInternalDTO
    {
        [Newtonsoft.Json.JsonProperty("capture_interval_in_seconds", Required = Newtonsoft.Json.Required.Default)]
        public int CaptureIntervalInSeconds { get; set; } = default!;

        [Newtonsoft.Json.JsonProperty("mode", Required = Newtonsoft.Json.Required.Default)]
        [Newtonsoft.Json.JsonConverter(typeof(StreamVideo.Core.Serialization.EnumeratedStructConverter<FrameRecordingSettingsRequestModeInternalEnumDTO>))]
        public FrameRecordingSettingsRequestModeInternalEnumDTO Mode { get; set; } = default!;

        [Newtonsoft.Json.JsonProperty("quality", Required = Newtonsoft.Json.Required.Default, NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
        public string Quality { get; set; } = default!;

    }

}

