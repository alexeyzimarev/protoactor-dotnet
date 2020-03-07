﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Xunit;

namespace Proto.Remote.Tests
{
    public class RemoteManager : IDisposable
    {
        private static string DefaultNodeAddress = "127.0.0.1:12000";
        private Dictionary<string, System.Diagnostics.Process> Nodes = new Dictionary<string, System.Diagnostics.Process>();

        public (string Address, System.Diagnostics.Process Process) DefaultNode => (DefaultNodeAddress, Nodes[DefaultNodeAddress]);

        public RemoteManager()
        {
            Serialization.RegisterFileDescriptor(Messages.ProtosReflection.Descriptor);
            ProvisionNode();
            EnsureRemote();
            Thread.Sleep(3000);
        }

        private static bool remoteStarted;

        private static void EnsureRemote()
        {
            if (remoteStarted) return;
            
            var config = new RemoteConfig
            {
                EndpointWriterOptions = new EndpointWriterOptions
                {
                    MaxRetries = 2,
                    RetryBackOffms = 10,
                    RetryTimeSpan = TimeSpan.FromSeconds(120)
                }
            };
            
            Remote.Start("127.0.0.1", 12001, config);
            remoteStarted = true;
        }

        public void Dispose()
        {
            foreach (var (_, process) in Nodes)
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }

            if (remoteStarted)
                Remote.Shutdown(false).GetAwaiter().GetResult();
        }

        public (string Address, System.Diagnostics.Process Process) ProvisionNode(string host = "127.0.0.1", int port = 12000)
        {
            var address = $"{host}:{port}";
            var buildConfig = "Debug";
#if RELEASE
            buildConfig = "Release";
#endif
            var nodeAppPath = Path.Combine("Proto.Remote.Tests.Node", "bin", buildConfig, "netcoreapp3.1", "Proto.Remote.Tests.Node.dll");
            var currentDirectory = Directory.GetCurrentDirectory();
            var testsDirectory = Directory.GetParent(currentDirectory).Parent.Parent.Parent;
            var nodeDllPath = Path.Combine(testsDirectory.FullName, nodeAppPath);
            
            Console.WriteLine(currentDirectory);
            Console.WriteLine(testsDirectory);
            Console.WriteLine(nodeDllPath);

            if (!File.Exists(nodeDllPath))
            {
                throw new FileNotFoundException(nodeDllPath);
            }

            var process = new System.Diagnostics.Process
            {
                StartInfo =
                {
                    Arguments = $"{nodeDllPath} --host {host} --port {port}",
                    CreateNoWindow = false,
                    UseShellExecute = false,
                    FileName = "dotnet"
                }
            };

            process.Start();
            Nodes.Add(address, process);

            Console.WriteLine($"Waiting for remote node {address} to initialise...");
            Thread.Sleep(TimeSpan.FromSeconds(3));

            return (address, process);
        }
    }

    [CollectionDefinition("RemoteTests")]
    public class RemoteCollection : ICollectionFixture<RemoteManager> { }
}