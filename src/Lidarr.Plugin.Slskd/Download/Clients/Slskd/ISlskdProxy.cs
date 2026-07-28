using System.Collections.Generic;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Download.Clients.Slskd;

public interface ISlskdProxy
{
    bool TestConnectivity(SlskdSettings settings);

    SlskdOptions GetOptions(SlskdSettings settings);

    Application GetApplication(SlskdSettings settings);

    bool SupportsBatches(SlskdSettings settings);

    List<DownloadClientItem> GetQueue(SlskdSettings settings);

    string Download(string searchId, string username, string downloadPath, string identifier, string albumTitle, SlskdSettings settings);

    void RemoveFromQueue(string downloadId, bool deleteData, SlskdSettings settings);
}
