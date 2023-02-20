﻿using EasilyNET.DependencyInjection.Contexts;

namespace EasilyNET.DependencyInjection.Abstractions;

/// <summary>
/// 应用初始化接口
/// </summary>
internal interface IApplicationInitialization
{
    /// <summary>
    /// 应用初始化
    /// </summary>
    /// <param name="context"></param>
    void ApplicationInitialization(ApplicationContext context);
}