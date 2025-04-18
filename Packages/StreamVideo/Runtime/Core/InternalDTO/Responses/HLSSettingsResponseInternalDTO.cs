//----------------------
// <auto-generated>
//     Generated using the NSwag toolchain v13.20.0.0 (NJsonSchema v10.9.0.0 (Newtonsoft.Json v10.0.0.0)) (http://NSwag.org)
// </auto-generated>
//----------------------

#nullable enable


using StreamVideo.Core.InternalDTO.Requests;
using StreamVideo.Core.InternalDTO.Events;
using StreamVideo.Core.InternalDTO.Models;

namespace StreamVideo.Core.InternalDTO.Responses
{
    using System = global::System;

    /// <summary>
    /// HLSSettings is the payload for HLS settings
    /// </summary>
    [System.CodeDom.Compiler.GeneratedCode("NJsonSchema", "13.20.0.0 (NJsonSchema v10.9.0.0 (Newtonsoft.Json v10.0.0.0))")]
    internal partial class HLSSettingsResponseInternalDTO
    {
        [Newtonsoft.Json.JsonProperty("auto_on", Required = Newtonsoft.Json.Required.Default)]
        public bool AutoOn { get; set; } = default!;

        [Newtonsoft.Json.JsonProperty("enabled", Required = Newtonsoft.Json.Required.Default)]
        public bool Enabled { get; set; } = default!;

        [Newtonsoft.Json.JsonProperty("quality_tracks", Required = Newtonsoft.Json.Required.Default)]
        public System.Collections.Generic.List<string> QualityTracks { get; set; } = new System.Collections.Generic.List<string>();

    }

}

