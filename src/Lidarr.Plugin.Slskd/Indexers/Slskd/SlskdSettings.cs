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
            RuleFor(c => c.MinimumPeerUploadSpeed).GreaterThanOrEqualTo(0);
            RuleFor(c => c.MaximumPeerQueueLength).GreaterThanOrEqualTo(0);
        }
    }

    public class SlskdIndexerSettings : IIndexerSettings
    {
        private static readonly SlskdIndexerSettingsValidator Validator = new SlskdIndexerSettingsValidator();

        [FieldDefinition(0, Label = "URL", HelpText = "URL of your slskd instance")]
        public string BaseUrl { get; set; } = "http://localhost:5030/";

        [FieldDefinition(1, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; } = "";

        [FieldDefinition(2, Type = FieldType.Number, Label = "Early Download Limit", Unit = "days", HelpText = "Days before release date to allow downloads. Empty is no limit", Advanced = true)]
        public int? EarlyReleaseLimit { get; set; }

        [FieldDefinition(3, Type = FieldType.KeyValueList, Label = "Ignored Users", HelpText = "Additional users to skip when searching. Your own slskd account is already excluded. Keys are just labels")]
        public IEnumerable<KeyValuePair<string, string>> IgnoredUsers { get; set; }

        [FieldDefinition(4, Type = FieldType.Number, Label = "Minimum Upload Speed", Unit = "MB/s", HelpText = "Hide results from users uploading slower than this. Decimals allowed, 0 shows everyone", Advanced = true)]
        public double MinimumPeerUploadSpeed { get; set; }

        [FieldDefinition(5, Type = FieldType.Number, Label = "Maximum Queue Length", HelpText = "Hide results from users with more uploads queued than this. 0 shows everyone", Advanced = true)]
        public int MaximumPeerQueueLength { get; set; }

        [FieldDefinition(6, Type = FieldType.Checkbox, Label = "Allow Incomplete Releases", HelpText = "Stop rejecting results with fewer audio files than the album has tracks. Either way nothing is hidden: a rejected result still appears in interactive search with its reason and can still be grabbed by hand. Off, it is only kept out of automatic grabs, where it would download in full and then fail to import", Advanced = true)]
        public bool AllowIncompleteReleases { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
