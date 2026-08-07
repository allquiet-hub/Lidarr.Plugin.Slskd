using System;
using FluentValidation;
using NzbDrone.Common.Extensions;
using NzbDrone.Core.Annotations;
using NzbDrone.Core.ThingiProvider;
using NzbDrone.Core.Validation;

namespace NzbDrone.Core.Download.Clients.Slskd
{
    public class SlskdSettingsValidator : AbstractValidator<SlskdSettings>
    {
        public SlskdSettingsValidator()
        {
            RuleFor(c => c.Host).ValidHost();
            RuleFor(c => c.Port).InclusiveBetween(1, 65535);
            RuleFor(c => c.UrlBase).ValidUrlBase().When(c => c.UrlBase.IsNotNullOrWhiteSpace());
            RuleFor(c => c.ApiKey).NotEmpty();
        }
    }

    public class SlskdSettings : IProviderConfig
    {
        private static readonly SlskdSettingsValidator Validator = new SlskdSettingsValidator();

        [FieldDefinition(0, Label = "Host", Type = FieldType.Textbox)]
        public string Host { get; set; } = "localhost";

        [FieldDefinition(1, Label = "Port", Type = FieldType.Textbox)]
        public int Port { get; set; } = 5030;

        [FieldDefinition(2, Label = "Url Base", Type = FieldType.Textbox, Advanced = true, HelpText = "Adds a prefix to the slskd url, e.g. http://[host]:[port]/[urlBase]/api")]
        public string UrlBase { get; set; }

        [FieldDefinition(3, Label = "Use SSL", Type = FieldType.Checkbox)]
        public bool UseSsl { get; set; }

        [FieldDefinition(4, Label = "API Key", Type = FieldType.Textbox, Privacy = PrivacyLevel.ApiKey)]
        public string ApiKey { get; set; } = "";

        [FieldDefinition(5, Label = "Fix slskd Config on Test", Type = FieldType.Checkbox, HelpText = "When Test finds slskd settings that break the integration, rewrite slskd's config file and restart it if required. The restart is skipped while slskd has active downloads. Needs an API key with the administrator role and 'remote_configuration' enabled in slskd")]
        public bool RepairConfiguration { get; set; }

        public NzbDroneValidationResult Validate()
        {
            return new NzbDroneValidationResult(Validator.Validate(this));
        }
    }
}
