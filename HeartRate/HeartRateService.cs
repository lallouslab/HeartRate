using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Storage.Streams;

namespace HeartRate
{
    enum ContactSensorStatus
    {
        NotSupported,
        NotSupported2,
        NoContact,
        Contact
    }

    class HeartRateService : IDisposable
    {
        // https://www.bluetooth.com/specifications/gatt/viewer?attributeXmlFile=org.bluetooth.characteristic.heart_rate_measurement.xml
        private const int m_heartRateMeasurementCharacteristicId = 0x2A37;

        private GattDeviceService m_service;
        private readonly object m_disposeSync = new object();
        private bool m_isDisposed;

        public event HeartRateUpdateEventHandler HeartRateUpdated;
        public delegate void HeartRateUpdateEventHandler(ContactSensorStatus status, int bpm);

        public void InitiateDefault()
        {
            var heartrateSelector = GattDeviceService.GetDeviceSelectorFromUuid(GattServiceUuids.HeartRate);

            var devices = AsyncResult(DeviceInformation.FindAllAsync(heartrateSelector));

            var device = devices.FirstOrDefault();

            if (device == null)
            {
                throw new ArgumentOutOfRangeException(
                    "Unable to locate heart rate device.");
            }

            GattDeviceService service;

            lock (m_disposeSync)
            {
                if (m_isDisposed)
                    throw new ObjectDisposedException(GetType().Name);

                Cleanup();

                service = AsyncResult(GattDeviceService.FromIdAsync(device.Id));

                m_service = service;
            }

            // Get heart rate characteristic
            var heartrate = service.GetCharacteristics(
                GattDeviceService.ConvertShortIdToUuid(m_heartRateMeasurementCharacteristicId)
                ).FirstOrDefault();

            if (heartrate == null)
            {
                throw new ArgumentOutOfRangeException(
                    $"Unable to locate heart rate measurement on device {device.Name} ({device.Id}).");
            }

            var status = AsyncResult(
                heartrate.WriteClientCharacteristicConfigurationDescriptorAsync(
                    GattClientCharacteristicConfigurationDescriptorValue.Notify));

            heartrate.ValueChanged += HeartRate_ValueChanged;

            Debug.WriteLine($"Started {status}");
        }

        private void HeartRate_ValueChanged(
            GattCharacteristic sender,
            GattValueChangedEventArgs args)
        {
            var value = args.CharacteristicValue;

            if (value.Length == 0)
                return;

            using (var reader = DataReader.FromBuffer(value))
            {
                var bpm = -1;
                /*
                Flags: bit 0, 1 bit
                    0 = Heart Rate Value Format is set to UINT8. Units: beats per minute (bpm)                 
                    1 = Heart Rate Value Format is set to UINT16. Units: beats per minute (bpm)
                 */
                var Flags = reader.ReadByte();
                var bIsUint16 = (Flags & 1) == 1;
                /*
                Flags: bit 1, 2 bits
                    0 = Sensor Contact feature is not supported in the current connection
                    1 = Sensor Contact feature is not supported in the current connection
                    2 = Sensor Contact feature is supported, but contact is not detected
                    3 = Sensor Contact feature is supported and contact is detected
                */
                var contactSensor = (ContactSensorStatus)((Flags >> 1) & 3);
                var minLength = bIsUint16 ? 3 : 2;

                if (value.Length < minLength)
                {
                    Debug.WriteLine($"Buffer was too small. Got {value.Length}, expected {minLength}.");
                    return;
                }

                if (value.Length > 1)
                {
                    bpm = bIsUint16
                        ? reader.ReadUInt16()
                        : reader.ReadByte();
                }

                Debug.WriteLine($"Read {Flags.ToString("X")} {contactSensor} {bpm}");

                HeartRateUpdated?.Invoke(contactSensor, bpm);
            }
        }

        private void Cleanup()
        {
            var service = Interlocked.Exchange(ref m_service, null);

            if (service == null)
                return;

            try
            {
                service.Dispose();
            }
            catch { }
        }

        private static T AsyncResult<T>(IAsyncOperation<T> async)
        {
            while (true)
            {
                switch (async.Status)
                {
                    // Give some time after the async operation has started
                    case AsyncStatus.Started:
                        Thread.Sleep(100);
                        continue;
                    // Get the results
                    case AsyncStatus.Completed:
                        return async.GetResults();
                    case AsyncStatus.Error:
                        throw async.ErrorCode;
                    case AsyncStatus.Canceled:
                        throw new TaskCanceledException();
                }
            }
        }

        public void Dispose()
        {
            lock (m_disposeSync)
            {
                m_isDisposed = true;

                Cleanup();
            }
        }
    }
}
