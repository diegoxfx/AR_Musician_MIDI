using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assets.Scripts.Songs
{
    [JsonConverter(typeof(StringEnumConverter))]
    public enum SongDifficulty
    {
        Beginner,
        Easy,
        Medium,
        Hard,
        // [Description("Very hard")]
        VeryHard,
        Professional
    }
}
