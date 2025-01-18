# README

## Prerequisites 
This plugin enables Lidarr to search Soulseek using Slskd. You must have a working Lidarr installation from the plugins branch and a working Slskd installation to use this plugin.

To generate the Api Key necessary for the communication to the Slskd app follow the steps here:
https://github.com/slskd/slskd/blob/master/docs/config.md#authentication

## Installation

In Lidarr, navigate to `/system/plugins` and paste the GitHub URL of this repository, and select Install.  

```text
https://github.com/allquiet-hub/Lidarr.Plugin.Slskd
```

You can observe the progress in the lower left corner. The installation will take several seconds depending on available resources. 

> [!NOTE]  
> Docker installations of Lidarr will require restarting the container


## Configuration

Once the plugin is installed, Slskd can be added as a download client. 
- Navigate to `/settings/downloadclients`, and select the plus button under Download clients. Slskd will appear at the bottom under the Other section.
- Enter the correct hostname.
- Enter the API key
- Select the Test button.
- If the Test returns a green checkmark, select Save.

## Verification

TBD
