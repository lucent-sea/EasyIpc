using Microsoft.Extensions.Logging;
using System;
using System.Threading.Tasks;

namespace EasyIpc
{
    public interface IConnectionFactory
    {
        Task<IClient> CreateClient(string serverName, string pipeName);
        Task<IServer> CreateServer(string pipeName);
    }

    public class ConnectionFactory : IConnectionFactory
    {
        private readonly ICallbackStoreFactory _callbackFactory;
        private readonly ILoggerFactory _loggerFactory;

        public ConnectionFactory(ICallbackStoreFactory callbackFactory, ILoggerFactory loggerFactory)
        {
            _callbackFactory = callbackFactory;
            _loggerFactory = loggerFactory;
        }

        public async Task<IServer> CreateServer(string pipeName)
        {
            var server = new Server(_callbackFactory, _loggerFactory.CreateLogger<Server>());
            await server.Initialize(pipeName);
            return server;
        }
        public async Task<IClient> CreateClient(string serverName, string pipeName)
        {
            var client = new Client(_callbackFactory, _loggerFactory.CreateLogger<Client>());
            await client.Initialize(serverName, pipeName);
            return client;
        }
    }
}
