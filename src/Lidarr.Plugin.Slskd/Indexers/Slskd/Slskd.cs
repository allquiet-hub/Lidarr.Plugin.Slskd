using System;
using NLog;
using NzbDrone.Common.Cache;
using NzbDrone.Common.Http;
using NzbDrone.Core.Configuration;
using NzbDrone.Core.Download.Clients.Slskd;
using NzbDrone.Core.Parser;

namespace NzbDrone.Core.Indexers.Slskd
{
    public class Slskd : HttpIndexerBase<SlskdIndexerSettings>
    {
        public override string Name => "Slskd";
        public override string Protocol => nameof(SlskdDownloadProtocol);
        public override bool SupportsRss => false;
        public override bool SupportsSearch => true;
        public override int PageSize => 100;
        public override TimeSpan RateLimit => new TimeSpan(0);

        private readonly ISlskdProxy _slskdProxy;

        public Slskd(ICacheManager cacheManager,
            ISlskdProxy slskdProxy,
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
            return new SlskdRequestGenerator()
            {
                Settings = Settings,
                Logger = _logger
            };
        }

        public override IParseIndexerResponse GetParser()
        {
            return new SlskdParser(Definition, Settings, RateLimit, _httpClient, _logger);
        }
    }
}
