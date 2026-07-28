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

        [FieldDefinition(3, Type = FieldType.Number, Label = "Search timeout", Unit = "seconds", HelpText = "Total time to spend on a single search before giving up on it and moving on", Advanced = true)]
        public int SearchTimeout { get; set; } = 15;

        [FieldDefinition(4, Type = FieldType.Number, Label = "Minimum download speed", Unit = "MB/s", HelpText = "All the users uploading at a lower speed will be filtered out", Advanced = true)]
        public int MinimumPeerUploadSpeed { get; set; } = 1;

        [FieldDefinition(5, Type = FieldType.KeyValueList, Label = "Ignored Users", HelpText = "All the users to be ignored when searching for media. Ideally you should input first your own username, to avoid redownloading stuff you already have. For Key you should use an incremental number.")]
        public IEnumerable<KeyValuePair<string, string>> IgnoredUsers { get; set; }

        [FieldDefinition(6, Type = FieldType.Number, Label = "Maximum peer queue length", HelpText = "Ignore users whose upload queue is longer than this, since those downloads would sit queued for hours. 0 disables the check", Advanced = true)]
        public int MaximumPeerQueueLength { get; set; } = 100;

        [FieldDefinition(7, Type = FieldType.Number, Label = "Response limit", HelpText = "Stop a search early once this many users have responded. Lower values finish popular searches faster but see fewer candidates", Advanced = true)]
        public int ResponseLimit { get; set; } = 50;

        [FieldDefinition(8, Type = FieldType.Checkbox, Label = "Allow incomplete releases", HelpText = "Offer results that contain fewer audio files than the album has tracks. These are rejected by default because Lidarr fails to import them with 'Has missing tracks'; they stay visible in interactive search and can still be grabbed manually", Advanced = true)]
        public bool AllowIncompleteReleases { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
