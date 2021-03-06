﻿using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Host.Listeners;
using Microsoft.Extensions.Logging;
using WeatherMap.Entities;
using WeatherMap.Services;

namespace Extensions.Triggers
{
    public class WeatherTriggerListener : IListener
    {
        private readonly Guid _instanceId = Guid.NewGuid();

        private readonly ITriggeredFunctionExecutor _executor;
        private CancellationTokenSource _listenerStoppingTokenSource;

        private readonly IWeatherService _weatherService;
        private readonly WeatherTriggerAttribute _attribute;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<WeatherTriggerListener> _logger;

        private Task _listenerTask;

        public WeatherTriggerListener(ITriggeredFunctionExecutor executor,
            IWeatherService weatherService, WeatherTriggerAttribute attribute, ILoggerFactory loggerFactory)
        {
            this._executor = executor;
            this._weatherService = weatherService;
            this._attribute = attribute;
            this._loggerFactory = loggerFactory;
            
            this._logger = this._loggerFactory.CreateLogger<WeatherTriggerListener>();
        }

        public void Cancel()
        {
            StopAsync(CancellationToken.None).Wait();
        }

        public void Dispose()
        {
            StopAsync(CancellationToken.None).Wait();
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            this._logger.LogDebug($"[{this.GetType().Name} ({this._instanceId.ToSmallString()})] - Starting Listener");
            try
            {
                _listenerStoppingTokenSource = new CancellationTokenSource();
                var factory = new TaskFactory();
                var token = _listenerStoppingTokenSource.Token;
                _listenerTask = factory.StartNew(async () => await ListenerAction(token), token);
            }
            catch (Exception)
            {
                throw;
            }

            return _listenerTask.IsCompleted ? _listenerTask : Task.CompletedTask;
        }

        private async Task ListenerAction(CancellationToken token)
        {
            this._weatherService.ApiKey = this._attribute.ApiKey;
            var cityData = new CityInfo();
            double? lastTemperature = null;

            while (!token.IsCancellationRequested)
            {
                this._logger.LogDebug($"[{this.GetType().Name} ({this._instanceId.ToSmallString()})] - GetCityInfoAsync for {this._attribute.CityName} start");
                try
                {
                    cityData = await this._weatherService.GetCityInfoAsync(this._attribute.CityName);
                }
                catch (Exception)
                {
                    cityData = null;
                }
                this._logger.LogDebug($"[{this.GetType().Name} ({this._instanceId.ToSmallString()})] - GetCityInfoAsync for {this._attribute.CityName} finish");

                if (!lastTemperature.HasValue ||
                    (cityData != null && Math.Abs(cityData.Temperature - lastTemperature.Value) > this._attribute.TemperatureThreshold))
                {
                    this._logger.LogDebug($"[{this.GetType().Name} ({this._instanceId.ToSmallString()})] - Function firing: lastTemperature={lastTemperature}, currentTemperature={cityData.Temperature} for {cityData.CityCode}");
                    
                    var weatherPayload = new WeatherPayload()
                    {
                        CityName = this._attribute.CityName,
                        CurrentTemperature = cityData.Temperature,
                        Timestamp = cityData.Timestamp,
                        LastTemperature = lastTemperature
                    };
                                        
                    await _executor.TryExecuteAsync(new TriggeredFunctionData() { TriggerValue = weatherPayload }, token);

                    lastTemperature = cityData.Temperature;
                }

                await Task.Delay(TimeSpan.FromSeconds(this._attribute.SecondsBetweenCheck), token);
            }
        }

        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_listenerTask == null)
                return;

            try
            {
                _listenerStoppingTokenSource.Cancel();
            }
            finally
            {
                await Task.WhenAny(_listenerTask, Task.Delay(Timeout.Infinite, cancellationToken));

            }
        }
    }
}