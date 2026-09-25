using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using FluentValidation.Results;
using NLog;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Slskd;
using NzbDrone.Core.Parser;
using NzbDrone.Core.Parser.Model;
using NzbDrone.Plugin.Slskd.Helpers;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Indexers.Slskd
{
    public class Slskd : HttpIndexerBase<SlskdIndexerSettings>
    {
        public override string Name => "Slskd";
        public override string Protocol => nameof(SlskdDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// How many times a search rejected because slskd was starting another one is offered again,
        /// and how long it waits between attempts. slskd holds its lock only for as long as it takes to
        /// accept a search, so a rejection clears almost immediately; the wait is generous because
        /// losing the query costs the whole fetch, while waiting costs a search that has not started.
        /// </summary>
        private const int SearchStartAttempts = 3;

        /// <summary>
        /// How many of a tier's queries are searched for at once. slskd builds its Soulseek client with
        /// maximumConcurrentSearches set to 2, a literal in its source rather than one of its options,
        /// so an instance cannot be asked what it allows: the number is matched here instead. Asking
        /// for more does not queue, it is refused; asking for less leaves one of the two slots idle.
        /// </summary>
        private const int SearchSlots = 2;

        private static readonly TimeSpan SearchStartRetryDelay = TimeSpan.FromSeconds(2);

        private readonly ISlskdProxy _slskdProxy;

        public Slskd(ISlskdProxy slskdProxy,
            IHttpClient httpClient,
            IIndexerStatusService indexerStatusService,
            IConfigService configService,
            IParsingService parsingService,
            Logger logger)
            : base(httpClient, indexerStatusService, configService, parsingService, logger)
        {
            _slskdProxy = slskdProxy;
        }

        public override IIndexerRequestGenerator GetRequestGenerator()
        {
            return new SlskdRequestGenerator(_logger, Settings);
        }

        public override IParseIndexerResponse GetParser()
        {
            return new SlskdParser(Definition, Settings, RateLimit, _httpClient, _logger);
        }

        /// <summary>
        /// Fetches the queries of a tier two at a time.
        ///
        /// slskd runs two outgoing searches at once and admits new ones through a lock it holds while a
        /// search waits for a free slot, so a third arriving alongside two running ones is refused with
        /// 429 rather than queued. A tier dispatched all at once therefore loses every query past the
        /// second - which an artist search, whose tier holds a query per album, cannot afford. Keeping
        /// two in flight fills slskd's capacity without ever asking for more than it has.
        ///
        /// This indexer's chains carry no pagination and no RSS state (every pageable request is a
        /// single search), which reduces the base loop to one call per request; what remains here is
        /// that loop, with tier semantics kept per album. A search of one album stops at the first
        /// tier that yields anything, as the base loop does. An artist search tags each request with
        /// the albums it looks for, and drops a request from later tiers once all of those albums have
        /// found something, so each album falls back on its own instead of all of them stopping as
        /// soon as any one is found.
        ///
        /// The result goes through CleanupReleases like the base implementation's does. That step is
        /// not cosmetic: it stamps the indexer onto every release, without which a grab is refused as
        /// having no indexer, and it is also what collapses the folders that more than one query of a
        /// tier found.
        ///
        /// Failures record through the indexer status service exactly so the backoff machinery keeps
        /// working; the base's finer-grained retry hints only apply to rate-limited HTTP indexers.
        /// </summary>
        protected override async Task<IList<ReleaseInfo>> FetchReleases(Func<IIndexerRequestGenerator, IndexerPageableRequestChain> pageableRequestChainSelector, bool isRecent = false)
        {
            var releases = new List<ReleaseInfo>();
            var foundAlbumIds = new HashSet<int>();
            var foundUntagged = false;

            try
            {
                var generator = GetRequestGenerator();
                var chain = pageableRequestChainSelector(generator);

                for (var i = 0; i < chain.Tiers; i++)
                {
                    var tier = chain.GetTier(i).SelectMany(pageable => pageable).ToList();
                    var requests = tier.Where(r => IsStillSearched(r, foundAlbumIds, foundUntagged)).ToList();

                    if (requests.Count < tier.Count)
                    {
                        _logger.Debug("Skipping {0} of the {1} queries of tier {2}, their albums already have results", tier.Count - requests.Count, tier.Count, i + 1);
                    }

                    if (requests.Count == 0)
                    {
                        continue;
                    }

                    using var slots = new SemaphoreSlim(SearchSlots, SearchSlots);

                    var pages = await Task.WhenAll(requests.Select(async request =>
                    {
                        await slots.WaitAsync();

                        try
                        {
                            // One parser per request: parsers are constructed per search and not shared
                            return await FetchPageWhenAccepted(request);
                        }
                        finally
                        {
                            slots.Release();
                        }
                    }));

                    for (var j = 0; j < requests.Count; j++)
                    {
                        var found = pages[j].Where(IsValidRelease).ToList();
                        if (found.Count == 0)
                        {
                            continue;
                        }

                        releases.AddRange(found);

                        if (requests[j] is SlskdIndexerRequest { AlbumIds.Count: > 0 } tagged)
                        {
                            foundAlbumIds.UnionWith(tagged.AlbumIds);
                        }
                        else
                        {
                            foundUntagged = true;
                        }
                    }
                }

                _indexerStatusService.RecordSuccess(Definition.Id);
            }
            catch (Exception ex)
            {
                _indexerStatusService.RecordFailure(Definition.Id);
                _logger.Warn(ex, "Error fetching releases from Slskd");
            }

            return CleanupReleases(releases, isRecent);
        }

        /// <summary>
        /// Whether a request still has an album to look for. A query shared by several albums runs as
        /// long as any of them has nothing yet, since the one search answers for all of them.
        /// </summary>
        private static bool IsStillSearched(IndexerRequest request, HashSet<int> foundAlbumIds, bool foundUntagged) =>
            request is SlskdIndexerRequest { AlbumIds.Count: > 0 } tagged
                ? tagged.AlbumIds.Any(id => !foundAlbumIds.Contains(id))
                : !foundUntagged;

        /// <summary>
        /// Runs one query, offering it again when slskd turns it down for being busy.
        ///
        /// Searches issued from here are already started one at a time, so a 429 means something else
        /// was starting one - another Lidarr search running alongside this one, or another application
        /// sharing the slskd instance. Retrying keeps that query, which would otherwise abort the whole
        /// fetch and record a failure against the indexer, taking it out of searches for a while.
        /// </summary>
        private async Task<IList<ReleaseInfo>> FetchPageWhenAccepted(IndexerRequest request)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    return await FetchPage(request, GetParser());
                }
                catch (TooManyRequestsException) when (attempt < SearchStartAttempts)
                {
                    _logger.Debug("slskd was busy starting another search, retrying in {0}s", SearchStartRetryDelay.TotalSeconds);
                    await Task.Delay(SearchStartRetryDelay);
                }
            }
        }

        /// <summary>
        /// Tests the indexer against what slskd reports about itself, rather than the base
        /// implementation's approach of running a canned query and requiring it to return something.
        ///
        /// A query proves nothing here that this does not, and costs more: it takes a real Soulseek
        /// round trip, and it fails whenever nobody happens to be sharing the track it looks for, which
        /// surfaces as a configuration error while the configuration is fine. Being logged in to the
        /// network is the condition that decides whether any search can return results at all, and it
        /// is the one thing a query failure never distinguishes from an empty network.
        /// </summary>
        protected override async Task<ValidationFailure> TestConnection()
        {
            Application application;

            try
            {
                var request = new HttpRequestBuilder(Settings.BaseUrl)
                    .Resource("api/v0/application")
                    .Accept(HttpAccept.Json)
                    .SetHeader("X-API-Key", Settings.ApiKey)
                    .Build();

                application = new HttpResponse<Application>(await _httpClient.ExecuteAsync(request)).Resource;
            }
            catch (HttpException ex) when (ex.Response?.StatusCode == HttpStatusCode.Unauthorized)
            {
                return new ValidationFailure("ApiKey", "Invalid API key");
            }
            catch (Exception ex)
            {
                _logger.Warn(ex, "Unable to reach slskd");
                return new ValidationFailure("BaseUrl", "Could not connect to slskd");
            }

            if (application?.Server == null)
            {
                return new ValidationFailure(string.Empty, "slskd did not report its connection state");
            }

            if (!SlskdCapabilities.IsSupported(application.Version))
            {
                return new ValidationFailure(string.Empty,
                    $"Slskd {SlskdCapabilities.MinimumVersion} or newer is required, this instance reports '{application.Version?.Current}'. Upgrade slskd and test again.");
            }

            if (!application.Server.IsLoggedIn)
            {
                return new ValidationFailure(string.Empty, $"slskd is not logged in to the Soulseek network, it reports '{application.Server.State}'");
            }

            return null;
        }
    }
}
