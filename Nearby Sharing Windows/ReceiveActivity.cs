﻿using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Content;
using AndroidX.AppCompat.App;
using AndroidX.RecyclerView.Widget;
using Google.Android.Material.ProgressIndicator;
using ShortDev.Android.UI;
using ShortDev.Microsoft.ConnectedDevices.Protocol;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Discovery;
using ShortDev.Microsoft.ConnectedDevices.Protocol.NearShare;
using ShortDev.Microsoft.ConnectedDevices.Protocol.Platforms;
using ShortDev.Networking;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using AndroidUri = Android.Net.Uri;
using ManifestPermission = Android.Manifest.Permission;

namespace Nearby_Sharing_Windows;

[Activity(Label = "@string/app_name", Theme = "@style/AppTheme", ConfigurationChanges = Constants.ConfigChangesFlags)]
public sealed class ReceiveActivity : AppCompatActivity, ICdpBluetoothHandler, INearSharePlatformHandler
{
    BluetoothAdapter? _btAdapter;
    BluetoothAdvertisement? _bluetoothAdvertisement;

    TextView debugLogTextView;

    [AllowNull] AdapterDescriptor<TranferToken> adapterDescriptor;
    [AllowNull] RecyclerView notificationsRecyclerView;
    readonly List<TranferToken> _notifications = new();
    protected override void OnCreate(Bundle savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // ToDo: Mac address settings
        // SetContentView(Resource.Layout.activity_mac_address);

        SetContentView(Resource.Layout.activity_receive);

        RequestPermissions(new[] {
            ManifestPermission.AccessFineLocation,
            ManifestPermission.AccessCoarseLocation,
            ManifestPermission.Bluetooth,
            ManifestPermission.BluetoothScan,
            ManifestPermission.BluetoothConnect,
            ManifestPermission.BluetoothAdvertise,
            ManifestPermission.AccessBackgroundLocation,
            ManifestPermission.ReadExternalStorage,
            ManifestPermission.WriteExternalStorage
        }, 0);

        notificationsRecyclerView = FindViewById<RecyclerView>(Resource.Id.notificationsRecyclerView)!;
        notificationsRecyclerView.SetLayoutManager(new LinearLayoutManager(this));

        adapterDescriptor = new(
            Resource.Layout.item_transfer_notification,
            (view, transfer) =>
            {
                var acceptButton = view.FindViewById<Button>(Resource.Id.acceptButton)!;
                var fileNameTextView = view.FindViewById<TextView>(Resource.Id.fileNameTextView)!;
                var detailsTextView = view.FindViewById<TextView>(Resource.Id.detailsTextView)!;

                if (transfer is FileTransferToken fileTransfer)
                {
                    fileNameTextView.Text = fileTransfer.FileName;
                    detailsTextView.Text = $"{fileTransfer.DeviceName} • {fileTransfer.FileSizeFormatted}";

                    var loadingProgressIndicator = view.FindViewById<CircularProgressIndicator>(Resource.Id.loadingProgressIndicator)!;
                    acceptButton.Click += (s, e) =>
                    {
                        fileTransfer.Accept(File.Create($"/sdcard/Download/{fileTransfer.FileName}"));
                        view.FindViewById(Resource.Id.actionsContainer)!.Visibility = Android.Views.ViewStates.Gone;
                        loadingProgressIndicator.Visibility = Android.Views.ViewStates.Visible;
                    };
                }
                else if (transfer is UriTranferToken uriTranfer)
                {
                    fileNameTextView.Text = uriTranfer.Uri;
                    detailsTextView.Text = uriTranfer.DeviceName;

                    acceptButton.Click += (s, e) =>
                    {
                        StartActivity(new Intent(Intent.ActionView, AndroidUri.Parse(uriTranfer.Uri)!));
                    };
                }

                view.FindViewById<Button>(Resource.Id.cancelButton)!.Click += (s, e) =>
                {
                    _notifications.Remove(transfer);
                    UpdateUI();

                    if (transfer is FileTransferToken fileTransfer)
                        fileTransfer.Cancel();
                };
            }
        );

        var service = (BluetoothManager)GetSystemService(BluetoothService)!;
        _btAdapter = service.Adapter!;

        string address = TryGetBtAddress(_btAdapter, out var exception) ?? "00:fa:21:3e:fb:19"; // "d4:38:9c:0b:ca:ae"; //

        FindViewById<TextView>(Resource.Id.deviceInfoTextView)!.Text = $"Visible as {_btAdapter.Name!}.\n" +
            $"Address: {address}";
        debugLogTextView = FindViewById<TextView>(Resource.Id.debugLogTextView)!;

        CdpAppRegistration.TryUnregisterApp<NearShareHandshakeApp>();
        CdpAppRegistration.TryRegisterApp<NearShareHandshakeApp>(() => new() { PlatformHandler = this });

        _bluetoothAdvertisement = new(this);
        _bluetoothAdvertisement.OnDeviceConnected += BluetoothAdvertisement_OnDeviceConnected;
        _bluetoothAdvertisement.StartAdvertisement(new CdpDeviceAdvertiseOptions(
            DeviceType.Android,
            PhysicalAddress.Parse(address.Replace(":", "").ToUpper()),
            _btAdapter.Name!
        ));
    }

    public override void Finish()
    {
        _bluetoothAdvertisement?.StopAdvertisement();
        base.Finish();
    }

    #region Communication
    public string? TryGetBtAddress(BluetoothAdapter adapter, out Exception? exception)
    {
        exception = null;

        try
        {
            var mServiceField = adapter.Class.GetDeclaredFields().FirstOrDefault((x) => x.Name.Contains("service", StringComparison.OrdinalIgnoreCase));
            if (mServiceField == null)
                throw new MissingFieldException("No service field found!");

            mServiceField.Accessible = true;
            var serviceProxy = mServiceField.Get(adapter)!;
            var method = serviceProxy.Class.GetDeclaredMethod("getAddress");
            if (method == null)
                throw new MissingMethodException("No method \"getAddress\"");

            method.Accessible = true;
            try
            {
                return (string?)method.Invoke(serviceProxy);
            }
            catch (Java.Lang.Reflect.InvocationTargetException ex)
            {
                if (ex.Cause == null)
                    throw;
                throw ex.Cause;
            }
        }
        catch (System.Exception ex)
        {
            exception = ex;
        }
        return null;
    }

    #region Not implemented
    public Task ScanBLeAsync(CdpScanOptions<CdpBluetoothDevice> scanOptions, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<CdpRfcommSocket> ConnectRfcommAsync(CdpBluetoothDevice device, CdpRfcommOptions options, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
    #endregion

    #region Advertisement
    public async Task AdvertiseBLeBeaconAsync(CdpAdvertiseOptions options, CancellationToken cancellationToken = default)
    {
        var settings = new AdvertiseSettings.Builder()
            .SetAdvertiseMode(AdvertiseMode.LowLatency)!
            .SetTxPowerLevel(AdvertiseTx.PowerHigh)!
            .SetConnectable(false)!
            .Build();

        var data = new AdvertiseData.Builder()
            .AddManufacturerData(options.ManufacturerId, options.BeaconData!)!
            .Build();

        BLeAdvertiseCallback callback = new();
        _btAdapter!.BluetoothLeAdvertiser!.StartAdvertising(settings, data, callback);

        await cancellationToken.AwaitCancellation();

        _btAdapter.BluetoothLeAdvertiser.StopAdvertising(callback);
    }

    class BLeAdvertiseCallback : AdvertiseCallback { }
    #endregion

    #region Rfcomm
    public async Task ListenRfcommAsync(CdpRfcommOptions options, CancellationToken cancellationToken = default)
    {
        using (var listener = _btAdapter!.ListenUsingInsecureRfcommWithServiceRecord(
            options.ServiceName,
            Java.Util.UUID.FromString(options.ServiceId)
        )!)
        {
            await Task.Run(() =>
            {
                while (true)
                {
                    var socket = listener.Accept();
                    if (cancellationToken.IsCancellationRequested)
                        return;

                    if (socket != null)
                        options!.OnSocketConnected!(socket.ToCdp());
                }
            }, cancellationToken);
            await cancellationToken.AwaitCancellation();
            listener.Close();
        }
    }

    private void BluetoothAdvertisement_OnDeviceConnected(CdpRfcommSocket socket)
    {
        Log(0, $"Device {socket.RemoteDevice!.Name} ({socket.RemoteDevice!.Address}) connected via rfcomm");
        Task.Run(() =>
        {
            using (BigEndianBinaryWriter writer = new(socket.OutputStream!))
            using (BigEndianBinaryReader reader = new(socket.InputStream!))
            {
                bool expectMessage;
                do
                {
                    CdpSession? session = null;
                    try
                    {
                        var header = CommonHeader.Parse(reader);
                        session = CdpSession.GetOrCreate(socket.RemoteDevice ?? throw new InvalidDataException(), header);
                        session.PlatformHandler = this;
                        expectMessage = session.HandleMessage(header, reader, writer);
                    }
                    catch (Exception ex)
                    {
                        Log(1, $"{ex.GetType().Name} in session {session?.LocalSessionId.ToString() ?? "null"} \n {ex.Message}");
                        throw;
                    }
                } while (expectMessage);
            }
        });
    }
    #endregion
    #endregion

    public void Log(int level, string message)
    {
        RunOnUiThread(() =>
        {
            debugLogTextView.Text += "\n" + $"[{DateTime.Now.ToString("HH:mm:ss")}]: {message}";
        });
    }

    void UpdateUI()
    {
        RunOnUiThread(() =>
        {
            notificationsRecyclerView.SetAdapter(adapterDescriptor.CreateRecyclerViewAdapter(_notifications));
        });
    }

    public void OnReceivedUri(UriTranferToken transfer)
    {
        _notifications.Add(transfer);
        UpdateUI();
    }

    public void OnFileTransfer(FileTransferToken transfer)
    {
        _notifications.Add(transfer);
        UpdateUI();
    }
}

static class Extensions
{
    public static CdpBluetoothDevice ToCdp(this BluetoothDevice @this, byte[]? beaconData = null)
        => new()
        {
            Address = @this.Address,
            Alias = @this.Alias,
            Name = @this.Name,
            BeaconData = beaconData
        };

    public static CdpRfcommSocket ToCdp(this BluetoothSocket @this)
        => new()
        {
            InputStream = @this.InputStream,
            OutputStream = @this.OutputStream,
            RemoteDevice = @this.RemoteDevice!.ToCdp(),
            Close = @this.Close
        };
}