﻿using MyUplinkSmartConnect.CostSavings;
using MyUplinkSmartConnect.CostSavingsRules;
using MyUplinkSmartConnect.ExternalPrice;
using MyUplinkSmartConnect.Models;
using MyUplinkSmartConnect.Services;
using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyUplinkSmartConnect
{
    internal class JobReScheuleheating
    {
        readonly MyUplinkService _myUplinkAPI;
        readonly MQTTService _mqttService;
        readonly CurrentStateService _currentState;

        public JobReScheuleheating(MyUplinkService myUplinkAPI, MQTTService mqttService, CurrentStateService currentState)
        {
            _myUplinkAPI = myUplinkAPI;
            _mqttService = mqttService;
            _currentState = currentState;
        }

        async Task<iBasePriceInformation?> GetPriceInformation()
        {
            var priceFetchApiList = new iBasePriceInformation[] { new EntsoeAPI(), new Nordpoolgroup(), new VgApi() };

            foreach(var priceListApi in priceFetchApiList)
            {
                var status = await priceListApi.GetPriceInformation();

                if(status && _currentState.PriceList.Count >= 48)
                {
                    Log.Logger.Debug("Using {priceApi} price list", priceListApi.GetType());
                    return priceListApi;
                }
            }

            Log.Logger.Warning("Failed to get price information for today and tomorrow, will check for todays prices");
            foreach (var priceListApi in priceFetchApiList)
            {
                var status = await priceListApi.GetPriceInformation();

                if (status && _currentState.PriceList.Count >= 24)
                {
                    Log.Logger.Debug("Using {priceApi} price list for today", priceListApi.GetType());
                    return priceListApi;
                }
            }

            Log.Logger.Warning("Failed to price list from all known apis, will check schedule later.");
            return null;
        }

        internal async Task<bool> Work()
        {
            var priceInformation = await GetPriceInformation();
            if (priceInformation == null)
                return false;

            bool hasTomorrowElectricityPrice = (_currentState.PriceList.Count >= 48);
            var cleanDate = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day);

            priceInformation.CreateSortedList(cleanDate, Settings.Instance.WaterHeaterMaxPowerInHours, Settings.Instance.WaterHeaterMediumPowerInHours);

            if(hasTomorrowElectricityPrice)
            {
                priceInformation.CreateSortedList(cleanDate.AddDays(1), Settings.Instance.WaterHeaterMaxPowerInHours, Settings.Instance.WaterHeaterMediumPowerInHours);
            }
            
            var group = await _myUplinkAPI.GetDevices();
            foreach (var device in group)
            {
                if (device.devices == null)
                {
                    Log.Logger.Error("Group({DeviceId}) does not have devices",device.id);
                    continue;
                }

                foreach (var tmpdevice in device.devices)
                {
                    var legionella = await ShouldRunLegionellaProgram(tmpdevice);

                    ICostSavingRules costSaving;
                    if(Settings.Instance.EnergiBasedCostSaving)
                    {
                        costSaving = new EnergiBasedRules();
                        Log.Logger.Debug("Using energi based rules for building heating schedules");
                    }
                    else
                    {
                        costSaving = new SimplePriceBasedRules();
                        Log.Logger.Debug("Using simple price based rules for building heating schedules");
                    }

                    var heaterModes = await _myUplinkAPI.GetCurrentModes(tmpdevice);
                    costSaving.WaterHeaterSchedule = await _myUplinkAPI.GetWheeklySchedules(tmpdevice);


                    var weekdayOrder = _myUplinkAPI.GetCurrentDayOrder(tmpdevice);

                    if (!_currentState.ModeLookup.ReCheckModes(heaterModes))
                    {
                        var status = await _myUplinkAPI.SetCurrentModes(tmpdevice, _currentState.ModeLookup.WaterHeaterModes);

                        if (!status)
                        {
                            Log.Logger.Error("Failed to update heater modes, aborting");
                            return false;
                        }
                    }                    

                    if (hasTomorrowElectricityPrice && costSaving.GenerateSchedule(weekdayOrder, legionella, cleanDate, cleanDate.AddDays(1))  || 
                        !hasTomorrowElectricityPrice && costSaving.GenerateSchedule(weekdayOrder, legionella, cleanDate))
                    {                        
#if DEBUG
                        costSaving.LogToCSV();
#endif
                        costSaving.LogSchedule();

                        var status = await _myUplinkAPI.SetWheeklySchedules(tmpdevice, costSaving.WaterHeaterSchedule);

                        if (!status)
                        {
                            Log.Logger.Error("Failed to update heater schedule, aborting");
                            return false;
                        }
                        else
                        {
                            if(hasTomorrowElectricityPrice)
                                Log.Logger.Information("Changed schedule for {DeviceId} for today and tomorrow",tmpdevice.id);
                            else
                                Log.Logger.Information("Changed schedule for {DeviceId} for today", tmpdevice.id);

                            if (!string.IsNullOrEmpty(Settings.Instance.MQTTServer) && !string.IsNullOrEmpty(device.name))
                            {
                                await _mqttService.SendUpdate(device.name, Models.CurrentPointParameterType.LastScheduleChangeInHours, Convert.ToInt32(0));
                            }
                            return hasTomorrowElectricityPrice; // If we did not get tomorrows prices we return false so we can try the schedule again.
                        }
                    }
                }
            }
            return false;
        }


        async Task<bool> ShouldRunLegionellaProgram(Device device)
        {
            Log.Logger.Debug("Should try to find schedule for legionella program");

            const int NextRunHoursBeforSchedule = 48;            
            var parameters = await _myUplinkAPI.GetDevicePoints(device, CurrentPointParameterType.LegionellaPreventionNext);

            if (!parameters.Any())
            {
                Log.Logger.Debug("No information returned from remote api");
                return false;
            }                

            foreach (var para in parameters)
            {
                var parm = (CurrentPointParameterType)int.Parse(para.parameterId ?? "0");
                switch (parm)
                {
                    case CurrentPointParameterType.LegionellaPreventionNext:
                        var shouldRun = (para.value <= NextRunHoursBeforSchedule);

                        Log.Logger.Debug("Next legionella program required to run in {h} hours, returning status: {status}", para.value, shouldRun);
                        return shouldRun;
                }
            }

            Log.Logger.Debug("Could not find matching parameter for CurrentPointParameterType.LegionellaPreventionNext");
            return false;
        }
    }
}