﻿using System;
using System.CommandLine;
using System.Diagnostics;
using System.IO;
using UnityWebBrowser.Engine.Shared.Communications;
using UnityWebBrowser.Engine.Shared.Core.Logging;
using UnityWebBrowser.Shared;
using UnityWebBrowser.Shared.Communications;
using UnityWebBrowser.Shared.Core;
using UnityWebBrowser.Shared.ReadWriters;
using VoltRpc.Communication;

namespace UnityWebBrowser.Engine.Shared.Core;

/// <summary>
///     Handles entry stuff for browser engines
/// </summary>
public abstract class EngineEntryPoint : IDisposable
{
    private Client ipcClient;
    private Host ipcHost;

    /// <summary>
    ///     Allows the engine to fire events on the Unity client side
    /// </summary>
    protected ClientActions ClientActions { get; private set; }

    /// <summary>
    ///     Is the <see cref="Client" /> side of the connection connected
    /// </summary>
    protected bool IsConnected => ipcClient.IsConnected;

    protected abstract bool ShouldInitLogger(LaunchArguments launchArguments, string[] args);

    /// <summary>
    ///     Do your early init stuff here
    /// </summary>
    protected abstract void EarlyInit(LaunchArguments launchArguments, string[] args);

    /// <summary>
    ///     Called when the arguments are parsed.
    ///     <para>Remember to lock if you don't want to immediately exit</para>
    /// </summary>
    /// <param name="launchArguments">Arguments as a <see cref="LaunchArguments" /></param>
    /// <param name="args">
    ///     Raw arguments inputted.
    ///     <para>
    ///         Should only need this if you start up multiple processes.
    ///     </para>
    /// </param>
    protected abstract void EntryPoint(LaunchArguments launchArguments, string[] args);

    /// <summary>
    ///     Call this in your engine's Program.Main method.
    /// </summary>
    /// <param name="args"></param>
    /// <returns></returns>
    public int Main(string[] args)
    {
        //We got a lot of arguments

        //Url to start with
        Option<string> initialUrl = new("-initial-url",
            () => "https://voltstro.dev",
            "The initial URL that the browser will first load to");

        //Resolution
        Option<int> width = new("-width",
            () => 1920,
            "The width of the window");
        Option<int> height = new("-height",
            () => 1080,
            "The height of the window");

        //General browser settings
        Option<bool> javaScript = new("-javascript",
            () => true,
            "Enable or disable javascript");
        Option<bool> webRtc = new("-web-rtc",
            () => false,
            "Enable or disable web RTC");
        Option<int> remoteDebugging = new("-remote-debugging",
            () => 0,
            "If the engine has remote debugging, what port to use (0 for disable)");
        Option<FileInfo> cachePath = new("-cache-path",
            () => null,
            "The path to the cache (null for no cache)");

        //Background color
        Option<Color> backgroundColor = new("-background-color",
            () => new Color("ffffffff"),
            "The color to use for the background");

        //Proxy settings
        Option<bool> proxyServer = new("-proxy-server",
            () => true,
            "Use a proxy server or direct connect");
        Option<string> proxyUsername = new("-proxy-username",
            () => null,
            "The username to use in the proxy auth");
        Option<string> proxyPassword = new("-proxy-password",
            () => null,
            "The password to use in the proxy auth");

        //IPC settings
        Option<FileInfo> communicationLayerPath = new("-comms-layer-path",
            () => null,
            "The location of where the dll for the communication layer is. If none is provided then the in-built TCP layer will be used.");
        Option<string> inLocation = new("-in-location",
            () => "5555",
            "In location for IPC (Pipes location or TCP port in TCP mode)");
        Option<string> outLocation = new("-out-location",
            () => "5556",
            "Out location for IPC (Pipes location or TCP port in TCP mode)");

        Option<FileInfo> logPath = new("-log-path",
            () => new FileInfo("engine.log"),
            "The path to where the log file will be");
        Option<LogSeverity> logSeverity = new("-log-severity",
            () => LogSeverity.Info,
            "The severity of the logs");

        RootCommand rootCommand = new()
        {
            initialUrl,
            width, height,
            javaScript, webRtc, remoteDebugging, cachePath,
            backgroundColor,
            proxyServer, proxyUsername, proxyPassword,
            communicationLayerPath, inLocation, outLocation,
            logPath, logSeverity
        };
        rootCommand.Description =
            "Unity Web Browser (UWB) Engine - Dedicated process for rendering with a browser engine.";
        //Some browser engines will launch multiple processes from the same process, they will most likely use custom arguments
        rootCommand.TreatUnmatchedTokensAsErrors = false;

        //The new version of System.CommandLine is very boiler platey
        LaunchArgumentsBinder launchArgumentBinder = new(
            initialUrl,
            width, height,
            javaScript, webRtc, remoteDebugging, cachePath,
            backgroundColor,
            proxyServer, proxyUsername, proxyPassword,
            communicationLayerPath, inLocation, outLocation,
            logPath, logSeverity);
        rootCommand.SetHandler((LaunchArguments parsedArgs) =>
        {
            if (ShouldInitLogger(parsedArgs, args))
                Logger.Init(parsedArgs.LogSeverity);

            //Is debug log enabled or not
            ClientActions = new ClientActions();

            //Run early init
            try
            {
                EarlyInit(parsedArgs, args);
            }
            catch (Exception)
            {
                Environment.Exit(-1);
                return;
            }

            //Run the entry point
            try
            {
                EntryPoint(parsedArgs, args);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Uncaught exception occured in the entry point!");
#if DEBUG
                Debugger.Break();
#endif
                Environment.Exit(-1);
            }

            Logger.Shutdown();
        }, launchArgumentBinder);

        //Invoke the command line parser and start the handler (the stuff above)
        return rootCommand.Invoke(args);
    }

    /// <summary>
    ///     Call when you are ready to setup the IPC
    /// </summary>
    /// <param name="engine"></param>
    /// <param name="arguments"></param>
    protected void SetupIpc(IEngine engine, LaunchArguments arguments)
    {
        try
        {
            ICommunicationLayer communicationLayer;
            if (arguments.CommunicationLayerPath == null)
            {
                //Use TCP
                Logger.Debug("No communication layer provided, using default TCP...");
                communicationLayer = new TCPCommunicationLayer();
            }
            else
            {
                communicationLayer = CommunicationLayerLoader.GetCommunicationLayerFromAssembly(
                    arguments.CommunicationLayerPath.FullName);
            }

            try
            {
                ipcHost = communicationLayer.CreateHost(arguments.InLocation);
                ipcClient = communicationLayer.CreateClient(arguments.OutLocation);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "An error occured setting up the communication layer!");
            }

            //Add type readers
            ReadWriterUtils.AddTypeReadWriters(ipcHost.TypeReaderWriterManager);
            ipcHost.AddService(typeof(IEngine), engine);
            ipcHost.StartListening();

            ReadWriterUtils.AddTypeReadWriters(ipcClient.TypeReaderWriterManager);
            ipcClient.AddService(typeof(IClient));

            //Connect the server (us) back to Unity
            try
            {
                ipcClient.Connect();
                ClientActions.SetIpcClient(ipcClient);
            }
            catch (ConnectionFailedException)
            {
                Logger.Error(
                    "The engine failed to connect back to the Unity client! Client events will not fire!");
                ipcClient.Dispose();
                ipcClient = null;
            }

            Logger.Debug("IPC Setup done.");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error setting up IPC!");
        }
    }

    /// <summary>
    ///     Call when you are ready
    /// </summary>
    protected void Ready()
    {
        ClientActions.Ready();
    }

    #region Destroy

    ~EngineEntryPoint()
    {
        ReleaseResources();
    }

    /// <summary>
    ///     Destroys this <see cref="EngineEntryPoint" /> instance
    /// </summary>
    public void Dispose()
    {
        ReleaseResources();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Called when <see cref="Dispose" /> is invoked
    /// </summary>
    protected virtual void ReleaseResources()
    {
        ClientActions.Dispose();
        ipcHost?.Dispose();
    }

    #endregion
}