﻿using MobileDeliveryClient.MessageTypes;
using MobileDeliveryLogger;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MobileDeliveryClient
{
    public partial class WebsocketClient
    {

        /// <summary>
        /// Force reconnection. 
        /// Closes current websocket stream and perform a new connection to the server.
        /// </summary>
        public async Task Reconnect()
        {
            if (!IsStarted)
            {
                Logger.Debug(L("Client not started, ignoring reconnection.."));
                return;
            }

            try
            {
                await ReconnectSynchronized(ReconnectionType.ByUser).ConfigureAwait(false);

            }
            finally
            {
                _reconnecting = false;
            }
        }


        private async Task ReconnectSynchronized(ReconnectionType type)
        {
            using (await _locker.LockAsync())
            {
                await Reconnect(type);
            }
        }

        private async Task Reconnect(ReconnectionType type)
        {
            IsRunning = false;
            if (_disposing)
                return;

            _reconnecting = true;
            if (type != ReconnectionType.Error)
                _disconnectedSubject.Raise(TranslateTypeToDisconnection(type));
            //_disconnectedSubject.OnNext(TranslateTypeToDisconnection(type));

            _cancellation.Cancel();
            _client?.Abort();
            _client?.Dispose();

            if (!IsReconnectionEnabled)
            {
                // reconnection disabled, do nothing
                IsStarted = false;
                _reconnecting = false;
                return;
            }

            Logger.Debug(L("Reconnecting..."));
            _cancellation = new CancellationTokenSource();
            await StartClient(_url, _cancellation.Token, type).ConfigureAwait(false);
            _reconnecting = false;
        }

        private void ActivateLastChance()
        {
            var timerMs = 1000 * 15;
            _lastChanceTimer = new Timer(LastChance, null, timerMs, timerMs);
        }

        private void DeactivateLastChance()
        {
            _lastChanceTimer?.Dispose();
            _lastChanceTimer = null;
        }

        private void LastChance(object state)
        {
            var timeoutMs = Math.Abs(ReconnectTimeoutMs);
            var diffMs = Math.Abs(DateTime.UtcNow.Subtract(_lastReceivedMsg).TotalMilliseconds);
            if (diffMs > timeoutMs)
            {
                if (!IsReconnectionEnabled)
                {
                    // reconnection disabled, do nothing
                    DeactivateLastChance();
                    return;
                }

                Logger.Debug(L($"Last message received more than {timeoutMs:F} ms ago. Hard restart.."));

                DeactivateLastChance();
#pragma warning disable 4014
                ReconnectSynchronized(ReconnectionType.NoMessageReceived);
#pragma warning restore 4014
            }
        }
    }
}
