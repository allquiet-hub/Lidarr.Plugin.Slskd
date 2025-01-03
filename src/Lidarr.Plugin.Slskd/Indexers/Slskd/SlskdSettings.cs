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
        public int? SearchTimeout { get; set; } = 15;

        [FieldDefinition(4, Type = FieldType.Number, Label = "Minimum download speed", Unit = "MB/s", HelpText = "ALl the users uploading at a lower speed will be filtered out", Advanced = true)]
        public int? MinimumPeerUploadSpeed { get; set; } = 1;

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
