using System.Collections.Generic;

namespace Readarr.Api.V1.Artist
{
    public class ArtistEditorDeleteResource
    {
        public List<int> ArtistIds { get; set; }
        public bool DeleteFiles { get; set; }
    }
}
