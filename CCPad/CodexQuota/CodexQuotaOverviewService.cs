using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CCPad.CodexQuota
{
    public sealed record CodexQuotaOverviewEntry(CodexQuotaProfile Profile, CodexQuotaSnapshot Snapshot);

    /// <summary>Read-only fan-out. Each profile owns its result, including failures.</summary>
    public sealed class CodexQuotaOverviewService
    {
        private readonly Func<string, CodexQuotaService> _createService;
        public CodexQuotaOverviewService(Func<string, CodexQuotaService>? createService = null)
            => _createService = createService ?? (home => new CodexQuotaService(home));

        public Task<CodexQuotaOverviewEntry[]> RefreshAsync(
            IReadOnlyList<CodexQuotaProfile> profiles,
            IProgress<CodexQuotaOverviewEntry>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.WhenAll(profiles.Select(profile => ReadAsync(profile, progress, cancellationToken)));

        private async Task<CodexQuotaOverviewEntry> ReadAsync(CodexQuotaProfile profile,
            IProgress<CodexQuotaOverviewEntry>? progress, CancellationToken cancellationToken)
        {
            CodexQuotaSnapshot snapshot;
            try
            {
                using var service = _createService(profile.Home);
                snapshot = await service.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                snapshot = CodexQuotaSnapshot.Loading();
            }
            catch
            {
                snapshot = CodexQuotaSnapshot.ErrorSnapshot("Could not read Codex usage");
            }
            var entry = new CodexQuotaOverviewEntry(profile, snapshot);
            progress?.Report(entry);
            return entry;
        }
    }
}
