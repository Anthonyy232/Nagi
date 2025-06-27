using System.Threading;
using System.Threading.Tasks;
using Nagi.Services.Data;

namespace Nagi.Services.Abstractions {
    public interface ILastFmService {
        Task<ArtistInfo?> GetArtistInfoAsync(string artistName, CancellationToken cancellationToken = default);
    }
}