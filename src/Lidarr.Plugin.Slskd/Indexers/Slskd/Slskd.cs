using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
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
        /// Fetches the queries of a tier concurrently instead of one after the other.
        ///
        /// slskd executes exactly two outgoing searches at a time and queues the rest, so the base
        /// implementation's sequential loop always leaves the second slot idle: the second query of a
        /// tier only starts once the first has completed and parsed.
        /// This indexer's chains carry no pagination and no RSS state (every pageable request is a
        /// single search), which reduces the base loop to one call per request; dispatching those
        /// calls together is therefore equivalent apart from wall time. Tier semantics are kept: a
        /// tier that yields anything still stops the chain.
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

            try
            {
                var generator = GetRequestGenerator();
                var chain = pageableRequestChainSelector(generator);

                for (var i = 0; i < chain.Tiers; i++)
                {
                    var requests = chain.GetTier(i).SelectMany(pageable => pageable).ToList();

                    // One parser per request: parsers are constructed per search and not shared
                    var pages = await Task.WhenAll(requests.Select(request => FetchPage(request, GetParser())));

                    releases.AddRange(pages.SelectMany(page => page).Where(IsValidRelease));

                    if (releases.Any())
                    {
                        break;
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
