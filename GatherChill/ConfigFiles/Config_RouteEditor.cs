using System;
using System.Collections.Generic;
using System.Text;

namespace GatherChill.ConfigFiles;

public partial class Config
{
    public string SaveLocation { get; set; } = Svc.PluginInterface.GetPluginConfigDirectory();
    public bool LoadExternalRoutes { get; set; } = true;
    public bool SaveOnRouteChange { get; set; } = true;
}
