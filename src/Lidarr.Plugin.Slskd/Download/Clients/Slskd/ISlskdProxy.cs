using System.Collections.Generic;
using NzbDrone.Plugin.Slskd.Models;

namespace NzbDrone.Core.Download.Clients.Slskd;

public interface ISlskdProxy
{
    bool TestConnectivity(SlskdSettings settings);

    SlskdOptions GetOptions(SlskdSettings settings);

    List<DownloadClientItem> GetQueue(SlskdSettings settings);

    string Download(string searchId, string username, string downloadPath, SlskdSettings settings);

    void RemoveFromQueue(string downloadId, bool deleteData, SlskdSettings settings);
}
