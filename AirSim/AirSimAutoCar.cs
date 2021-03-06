using System;
using System.Linq;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using VePack;
using VePack.Utilities;
using VePack.Utilities.Geometry;
using VePack.Utilities.IO;
using VePack.Utilities.NeuralNetwork;
using VePack.Utilities.NeuralNetwork.Cmac;
using VePack.Plugin.Navigation;
using VePack.Plugin.Controllers.ModelFree;
using VePack.Plugin.Controllers.ModelBased.Steering;
using VePack.Plugin.Filters.Sensor;
using VePack.Connectors.Imu;

namespace AirSim
{
    public sealed class AirSimAutoCar : IVehicle<CarInformation>
    {

        // ------ fields ------ //

        private readonly Rootobject _config;
        private readonly AirSimConnector _car;
        private readonly Process _python;
        private readonly TcpSocketClient _client;
        private readonly BidirectionalDataStream _stream;
        private readonly Pid _speedController;
        private readonly GeometricSteeringModel _steerModel;
        private readonly ISteeringController _steerController;
        private readonly LsmHeadingCorrector _imuFilter;
        private readonly IDisposable _connector;
        private double _targetSpeed;
        private MapNavigator _navigator;
        private CarOperation _operation;
        private CancellationTokenSource _cts;


        // ------ properties ------ //

        public IObservable<CompositeInfo<CarInformation>> InfoUpdated { get; }


        // ------ constructors ------ //

        public AirSimAutoCar()
        {
            _config = new ConfigurationBuilder().AddJsonFile("rootsettings.json").Build().Get<Rootobject>();
            _cts = new();
            _cts.Cancel();
            _python = new() { StartInfo = new(_config.PythonExe) { Arguments = _config.PyFile} };
            _python.Start();
            _client = new("127.0.0.1", 3000);
            _stream = _client.GetStream();
            _car = new(_stream);
            _imuFilter = new();
            _operation = new();
            _speedController = new(PidType.Speed, 0.001, 0, 0.003);
            _steerModel = _config.UseNN 
                ? new NnSteeringModel(
                    _config.SteeringModelFile.EndsWith(".ynn")
                        ? NetworkGraph.Load(_config.SteeringModelFile)
                        : CmacBundler.Load(_config.SteeringModelFile), 
                    false)
                : new GeometricSteeringModel(1.6, 0.4);


            _steerController = new PfcSteeringController(
                _steerModel,
                _config.PfcCoincidenceIndexes,
                (i, v) =>
                {
                    var convergenceTime = 1.5;  // 参照軌道とパスが収束する時間(s)
                    var sharpness = 0.2;        // S字の曲がり具合
                    var speed = _steerModel.VehicleSpeed;
                    var lateralE = speed < 0 ? -v[0] : v[0];
                    var headingE = Angle.FromRadian(v[1]);
                    var convergenceDistance = convergenceTime * Math.Abs(speed);
                    var margin = sharpness * convergenceDistance / 2;
                    var targetPosition = i * _steerModel.Dt / convergenceTime;
                    var lookAheadDistance = convergenceDistance * targetPosition;
                    var switchBack = _navigator.CurrentPath.Id is "Back" || _navigator.NextPath?.Id is "Back";
                    var startPoint = new TrajectoryPoint(new(lateralE, 0), headingE);
                    var endPoint = _navigator.GetLookAheadPointFromReferencePoint(lookAheadDistance, !switchBack);
                    if (targetPosition > 1)
                        return new double[] { endPoint.Position.X, endPoint.Heading.Radian, 0, 0 };
                    var (p, h) = NaviHelper.GetTrajectoryPointFromBezierCurve(startPoint, endPoint, targetPosition, margin);
                    if (speed < 0) p = new(-p.X, -p.Y);
                    return new double[] { p.X, h.Radian, 0, 0 };
                },
                1.0,
                Angle.FromDegree(35),
                Angle.FromDegree(10)
            );

            //_steerController = new LqrSteeringController(
            //    _steerModel,
            //    1, 1, 1,
            //    Angle.FromDegree(35),
            //    Angle.FromDegree(10)
            //);

            var line = "";
            if (_config.MapFile is not null && _config.MapFile is not "")
            {
                SetMap(_config.MapFile);
                var iniPoint = _navigator.MapData.Paths[0].Points[0];
                foreach (var path in _navigator.MapData.Paths)
                    foreach (var p in path.Points)
                        line += $"{p.Y - iniPoint.Y},{p.X - iniPoint.X},";
            }
            _stream.WriteString(line);
            _car.ConnectSendingStream(new Freq(100));

            var observable = _car.ConnectReceivingStream(new Freq(10))
                .Finally(Dispose)
                .Select(d =>
                {
                    var (x, y, _) = new WgsPointData(d.Gnss.Latitude, d.Gnss.Longitude).ToUtm();
                    var heading = _imuFilter.Correct(d.Imu.Yaw, new(x, y), d.SteeringAngle, d.VehicleSpeed / 3.6);
                    x -= (float)(_config.AntennaOffset * Math.Sin(heading.Radian));
                    y -= (float)(_config.AntennaOffset * Math.Cos(heading.Radian));
                    var imuData = new ImuData(DateTimeOffset.Now, heading);
                    return new CompositeInfo<CarInformation>(
                        _cts is not null && _cts.IsCancellationRequested is false,
                        d, d.Gnss, d.Imu, _navigator?.Update(new(x, y), heading)
                    );
                })
                .TakeWhile(x =>
                {
                    if (x?.Geo is not null)
                    {
                        return true;
                    }
                    else
                    {
                        Dispose();
                        return false;
                    }
                })
                .Publish();
            _connector = observable.Connect();
            InfoUpdated = observable;
        }


        // ------ public methods ------ //

        public void Dispose()
        {
            Stop();
            _connector?.Dispose();
            _car.Dispose();
            _stream.Dispose();
            _client.Dispose();
            _python.Dispose();
        }

        public void SetMap(string mapFile, string plnFile = null)
        {
            if (mapFile is null && mapFile is "")
                throw new ArgumentNullException(nameof(mapFile));
            var map = NaviHelper.LoadMapFromFile(mapFile).ModifyMapByPlnFile(plnFile);
            // 先に旋回パスも作っとく
            var stride = 1;
            for (int i = 0; i < map.Paths.Count - 1; i += stride)
            {
                if (map.Paths[i].Id is "Work" && _config.PathEndMargin > 0)
                    map.Paths[i].ExtendLast(_config.PathEndMargin);
                map.Paths[i + 1] = NaviHelper.ModifyPathDirection(map.Paths[i + 1], map.Paths[i].Points[^1]);
                var paths = NaviHelper.GenerateTurnPath(map.Paths[i], map.Paths[i + 1], _config.TurnRadius, 1);
                stride = 1;
                map.Paths.Insert(i + stride++, paths.FirstHalf);
                map.Paths.Insert(i + stride++, paths.Bridge);
                map.Paths.Insert(i + stride++, paths.SecondHalf);
            }
            _navigator = new(map) { AutoDirectionModification = false };
            _navigator.CurrentPathChanged.Finally(Dispose);
        }

        public async void Start()
        {

            _cts = new();
            _operation = new();

            InfoUpdated
               .Where(x => x?.Geo is not null && x?.Vehicle is not null)
               .Where(_ => _operation.FootBrake is 0)
               .TakeUntil(x => _cts.IsCancellationRequested)
               .Do(x =>
               {
                   var speedError = x.Vehicle.VehicleSpeed - _targetSpeed;
                   _operation.Throttle += _speedController.GetControlQuantity(speedError).InsideOf(-1, 1);
                   _car.Set(_operation);
               })
               .Subscribe();

            while (_config.AutoSteering && !_cts.IsCancellationRequested)
                await FollowAsync(_cts.Token);

        }

        public void Stop()
        {
            _operation = new() { FootBrake = 2 };
            _car.Set(_operation);
            if (!_cts.IsCancellationRequested)
                InfoUpdated.Where(x => x is not null).TakeUntil(x => x.Vehicle.VehicleSpeed < 0.1).ToTask().Wait();
            _cts?.Cancel();
        }

        public void SetVehicleSpeed(double speed)
        {
            _targetSpeed = speed;
            _operation.FootBrake = 0;
            _car.Set(_operation);
        }

        public void SetSteeringAngle(Angle steerAngle)
        {
            _operation.SteeringAngle = Angle.FromDegree(steerAngle.Degree.InsideOf(-35, 35));
            _car?.Set(_operation);
        }


        // ------private methods ------ //

        private void SetBrake(int level)
        {
            _operation.Throttle = 0;
            _operation.FootBrake = level;
            _car.Set(_operation);
        }

        private async Task<CompositeInfo<CarInformation>> FollowAsync(CancellationToken ct)
        {

            Console.WriteLine($"\nstart to follow path {_navigator.CurrentPathIndex}.\n");
            await InfoUpdated.TakeUntil(x => Math.Abs(x.Vehicle.VehicleSpeed) > 0.1).ToTask();

            if (_navigator.CurrentPath.Id is "Back")
            {
                SetSteeringAngle(Angle.Zero);
                SetBrake(2);
                await Task.Delay(200, ct);
                SetVehicleSpeed(-_targetSpeed);
                await InfoUpdated
                    .Where(x => x?.Geo is not null && x?.Vehicle is not null)
                    .TakeWhile(x => !ct.IsCancellationRequested)
                    .TakeUntil(_navigator.CurrentPathChanged)
                    .Do(x =>
                    {
                        var lateral = -x.Geo.LateralError;
                        var heading = x.Geo.HeadingError - Angle.FromRadian(Math.PI);
                        var steer = x.Vehicle.SteeringAngle;
                        var speed = x.Vehicle.VehicleSpeed / 3.6;
                        double.TryParse(_navigator.CurrentPoint.Id, out var curvature);
                        _steerModel.UpdateA(lateral, heading, steer, speed, curvature);
                        var angle = _steerController.GetSteeringAngle(lateral, heading, steer);
                        SetSteeringAngle(angle);
                        Console.Write($"Steer: {angle.Degree:f1} ... ");
                    })
                    .ToTask();
                await Task.Delay(200);
                SetBrake(2);
                await Task.Delay(200, ct);
                SetVehicleSpeed(-_targetSpeed);
            }

            return await InfoUpdated
                .Where(x => x?.Geo is not null && x?.Vehicle is not null)
                .TakeWhile(x => !ct.IsCancellationRequested)
                .TakeUntil(_navigator.CurrentPathChanged)
                .Do(x =>
                {
                    var lateral = x.Geo.LateralError;
                    var heading = x.Geo.HeadingError;
                    var steer = x.Vehicle.SteeringAngle;
                    var speed = x.Vehicle.VehicleSpeed / 3.6;
                    double.TryParse(_navigator.CurrentPoint.Id, out var curvature);
                    _steerModel.UpdateA(lateral, heading, steer, speed, curvature);
                    var angle = _steerController.GetSteeringAngle(lateral, heading, steer);
                    SetSteeringAngle(angle);
                    Console.Write($"Steer: {angle.Degree:f1} ... ");
                })
                .ToTask();

        }

    }
}
