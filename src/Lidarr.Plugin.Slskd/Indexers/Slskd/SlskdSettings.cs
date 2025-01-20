using System.Collections.Generic;
using FluentValidation;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Indexers.Slskd
{
    public class SlskdIndexerSettingsValidator : AbstractValidator<SlskdIndexerSettings>
    {
        public SlskdIndexerSettingsValidator()
        {
            RuleFor(c => c.BaseUrl).ValidRootUrl();
            RuleFor(c => c.ApiKey).NotEmpty();
            RuleFor(c => c.SearchTimeout).GreaterThan(0);
        }
    }

    public class SlskdIndexerSettings : IIndexerSettings
    {
        private static readonly SlskdIndexerSettingsValidator Validator = new SlskdIndexerSettingsValidator();

        [FieldDefinition(0, Label = "URL", HelpText = "The URL to your Slskd download client")]
        public string BaseUrl { get; set; } = "http://localhost:5030/";

        [FieldDefinition(1, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; } = "";

        [FieldDefinition(2, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Time before release date Lidarr will download from this indexer, empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(3, Type = FieldType.Number, Label = "Search timeout", Unit = "seconds", HelpText = "", Advanced = true)]
        public int SearchTimeout { get; set; } = 15;

        [FieldDefinition(4, Type = FieldType.Number, Label = "Minimum download speed", Unit = "MB/s", HelpText = "ALl the users uploading at a lower speed will be filtered out", Advanced = true)]
        public int MinimumPeerUploadSpeed { get; set; } = 1;

        [FieldDefinition(5, Type = FieldType.KeyValueList, Label = "Ignored Users", HelpText = "All the users to be ignored when searching for media. Ideally you should input first your own username, to avoid redownloading stuff you arleady have. For Key you should use an incremental number.")]
        public IEnumerable<KeyValuePair<string, string>> IgnoredUsers { get; set; }

        [FieldDefinition(6, Type = FieldType.Checkbox, Label = "Search results with less files than the album release with least tracks first", HelpText = "Example: if an album has a single release with 15 tracks, all results with 14 or less files will be filtered out. If no releases are found with less tracks, it will fallback to finding releases with any amount of tracks", Advanced = true)]
        public bool SearchResultsWithLessFilesThanAlbumFirst { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
