﻿using System;

namespace Sir.Store
{
    /// <summary>
    /// Teardown app.
    /// </summary>
    public class Stop : IPluginStop
    {
        public void OnApplicationShutdown(IServiceProvider serviceProvider)
        {
        }
    }
}
