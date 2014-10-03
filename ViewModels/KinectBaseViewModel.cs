using Microsoft.Kinect;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LookyEyes.ViewModels
{
    public class KinectBaseViewModel : ViewModelBase
    {

        #region class variables

        /// <summary>
        /// Size of the RGB pixel in the _kinectColorBitmap
        /// </summary>
        private readonly int bytesPerPixel = (PixelFormats.Bgr32.BitsPerPixel + 7) / 8;

        /// <summary>
        /// Active Kinect sensor
        /// </summary>
        private KinectSensor kinectSensor = null;

        /// <summary>
        /// Coordinate mapper to map one type of point to another
        /// </summary>
        private CoordinateMapper coordinateMapper = null;

        /// <summary>
        /// Reader for depth/color/body index frames
        /// </summary>
        private MultiSourceFrameReader multiFrameSourceReader = null;

        /// <summary>
        /// Bitmap to display
        /// </summary>
        private WriteableBitmap _kinectColorBitmap = null;

        /// <summary>
        /// The size in bytes of the _kinectColorBitmap back buffer
        /// </summary>
        private uint bitmapBackBufferSize = 0;

        /// <summary>
        /// Intermediate storage for the color to depth mapping
        /// </summary>
        private DepthSpacePoint[] colorMappedToDepthPoints = null;

        /// <summary>
        /// Constant for clamping Z values of camera space points from being negative
        /// </summary>
        private const float InferredZPositionClamp = 0.1f;


        /// <summary>
        /// Gets the _kinectColorBitmap to display
        /// </summary>
        public ImageSource ImageSource
        {
            get
            {
                return this._kinectColorBitmap;
            }
        }

        /// <summary>
        /// Current status text to display
        /// </summary>
        private string statusText = null;

        /// <summary>
        /// Gets or sets the current status text to display
        /// </summary>
        public string StatusText
        {
            get
            {
                return this.statusText;
            }

            set
            {
                if (this.statusText != value)
                {
                    this.SetProperty(ref statusText, value, () => this.StatusText);
                }
            }
        }


        private CameraSpacePoint _trackedHead;
        public CameraSpacePoint TrackedHead
        {
            get
            {
                if (_trackedHead == null)
                {
                    _trackedHead = new CameraSpacePoint();
                }
                return _trackedHead;
            }
            set
            {

                this.SetProperty(ref _trackedHead, value, () => this.TrackedHead);
            }
        }

        private int _trackedHeadX;
        public int TrackedHeadX
        {
            get
            {
                return _trackedHeadX;
            }
            set
            {

                this.SetProperty(ref _trackedHeadX, value, () => this.TrackedHeadX);
            }
        }

        private int _trackedHeadY;
        public int TrackedHeadY
        {
            get
            {
                return _trackedHeadY;
            }
            set
            {

                this.SetProperty(ref _trackedHeadY, value, () => this.TrackedHeadY);
            }
        }




        #endregion


        #region ViewModel Commands




        private CommandBase _screenshotCommand { get; set; }
        public CommandBase ScreenshotCommand
        {
            get
            {
                if (_screenshotCommand == null)
                {
                    _screenshotCommand = new CommandBase(i => ScreenshotCommandExecute(), null);
                }
                return _screenshotCommand;
            }
        }

        #endregion

        #region constructor singleton instance


        private KinectBaseViewModel()
        {

        }



        private static KinectBaseViewModel _instance;
        /// <summary>
        /// Singleton instance of this VM.
        /// </summary>
        public static KinectBaseViewModel Instance
        {

            get
            {
                if (_instance == null)
                {
                    _instance = new KinectBaseViewModel();

                    // for Alpha (and public beta!!! boo.) one sensor is supported
                    _instance.kinectSensor = KinectSensor.GetDefault();

                    if (_instance.kinectSensor != null)
                    {

                        _instance.multiFrameSourceReader = _instance.kinectSensor.OpenMultiSourceFrameReader(FrameSourceTypes.Depth | FrameSourceTypes.Color | FrameSourceTypes.BodyIndex | FrameSourceTypes.Body);

                        _instance.multiFrameSourceReader.MultiSourceFrameArrived += _instance.Reader_MultiSourceFrameArrived;

                        _instance.coordinateMapper = _instance.kinectSensor.CoordinateMapper;

                        FrameDescription depthFrameDescription = _instance.kinectSensor.DepthFrameSource.FrameDescription;

                        int depthWidth = depthFrameDescription.Width;
                        int depthHeight = depthFrameDescription.Height;

                        FrameDescription colorFrameDescription = _instance.kinectSensor.ColorFrameSource.FrameDescription;

                        int colorWidth = colorFrameDescription.Width;
                        int colorHeight = colorFrameDescription.Height;

                        _instance.colorMappedToDepthPoints = new DepthSpacePoint[colorWidth * colorHeight];

                        _instance._kinectColorBitmap = new WriteableBitmap(colorWidth, colorHeight, 96.0, 96.0, PixelFormats.Bgra32, null);

                        // Calculate the WriteableBitmap back buffer size
                        _instance.bitmapBackBufferSize = (uint)((_instance._kinectColorBitmap.BackBufferStride * (_instance._kinectColorBitmap.PixelHeight - 1)) + (_instance._kinectColorBitmap.PixelWidth * _instance.bytesPerPixel));

                        _instance.kinectSensor.IsAvailableChanged += _instance.Sensor_IsAvailableChanged;

                        _instance.kinectSensor.Open();

                        _instance.StatusText = _instance.kinectSensor.IsAvailable ? Properties.Resources.RunningStatusText
                                                                        : Properties.Resources.NoSensorStatusText;


                    }
                    else
                    {
                    }



                }

                return _instance;
            }
        }
        #endregion

        #region viewmodel event handlers
        private void Sensor_IsAvailableChanged(object sender, IsAvailableChangedEventArgs e)
        {
            Instance.StatusText = Instance.kinectSensor.IsAvailable ? Properties.Resources.RunningStatusText
                                                            : Properties.Resources.NoSensorStatusText;
        }

        /// <summary>
        /// Handles the depth/color/body index frame data arriving from the sensor
        /// </summary>
        /// <param name="sender">object sending the event</param>
        /// <param name="e">event arguments</param>
        private void Reader_MultiSourceFrameArrived(object sender, MultiSourceFrameArrivedEventArgs e)
        {
            int depthWidth = 0;
            int depthHeight = 0;

            DepthFrame depthFrame = null;
            ColorFrame colorFrame = null;
            BodyIndexFrame bodyIndexFrame = null;
            BodyFrame bodyFrame = null;

            bool isBitmapLocked = false;

            MultiSourceFrame multiSourceFrame = e.FrameReference.AcquireFrame();

            // If the Frame has expired by the time we process this event, return.
            if (multiSourceFrame == null)
            {
                return;
            }

            // We use a try/finally to ensure that we clean up before we exit the function.  
            // This includes calling Dispose on any Frame objects that we may have and unlocking the _kinectColorBitmap back buffer.
            try
            {
                depthFrame = multiSourceFrame.DepthFrameReference.AcquireFrame();
                colorFrame = multiSourceFrame.ColorFrameReference.AcquireFrame();
                bodyIndexFrame = multiSourceFrame.BodyIndexFrameReference.AcquireFrame();

                bodyFrame = multiSourceFrame.BodyFrameReference.AcquireFrame();
                if (bodyFrame != null)
                {
                    this.RenderBodyFrame(bodyFrame);
                }

                // If any frame has expired by the time we process this event, return.
                // The "finally" statement will Dispose any that are not null.
                if ((depthFrame == null) || (colorFrame == null) || (bodyIndexFrame == null))
                {
                    return;
                }

                // Process Depth
                FrameDescription depthFrameDescription = depthFrame.FrameDescription;

                depthWidth = depthFrameDescription.Width;
                depthHeight = depthFrameDescription.Height;

                // Access the depth frame data directly via LockImageBuffer to avoid making a copy
                using (KinectBuffer depthFrameData = depthFrame.LockImageBuffer())
                {
                    this.coordinateMapper.MapColorFrameToDepthSpaceUsingIntPtr(
                        depthFrameData.UnderlyingBuffer,
                        depthFrameData.Size,
                        this.colorMappedToDepthPoints);
                }

                // We're done with the DepthFrame 
                depthFrame.Dispose();
                depthFrame = null;

                // Process Color

                // Lock the _kinectColorBitmap for writing
                this._kinectColorBitmap.Lock();
                isBitmapLocked = true;

                colorFrame.CopyConvertedFrameDataToIntPtr(this._kinectColorBitmap.BackBuffer, this.bitmapBackBufferSize, ColorImageFormat.Bgra);

                // We're done with the ColorFrame 
                colorFrame.Dispose();
                colorFrame = null;

                // We'll access the body index data directly to avoid a copy
                using (KinectBuffer bodyIndexData = bodyIndexFrame.LockImageBuffer())
                {
                    unsafe
                    {
                        byte* bodyIndexDataPointer = (byte*)bodyIndexData.UnderlyingBuffer;

                        int colorMappedToDepthPointCount = this.colorMappedToDepthPoints.Length;

                        fixed (DepthSpacePoint* colorMappedToDepthPointsPointer = this.colorMappedToDepthPoints)
                        {
                            // Treat the color data as 4-byte pixels
                            uint* bitmapPixelsPointer = (uint*)this._kinectColorBitmap.BackBuffer;

                            //// Loop over each row and column of the color image
                            //// Zero out any pixels that don't correspond to a body index
                            for (int colorIndex = 0; colorIndex < colorMappedToDepthPointCount; ++colorIndex)
                            {
                                float colorMappedToDepthX = colorMappedToDepthPointsPointer[colorIndex].X;
                                float colorMappedToDepthY = colorMappedToDepthPointsPointer[colorIndex].Y;

                                // The sentinel value is -inf, -inf, meaning that no depth pixel corresponds to this color pixel.
                                if (!float.IsNegativeInfinity(colorMappedToDepthX) &&
                                    !float.IsNegativeInfinity(colorMappedToDepthY))
                                {
                                    // Make sure the depth pixel maps to a valid point in color space
                                    int depthX = (int)(colorMappedToDepthX + 0.5f);
                                    int depthY = (int)(colorMappedToDepthY + 0.5f);

                                    // If the point is not valid, there is no body index there.
                                    if ((depthX >= 0) && (depthX < depthWidth) && (depthY >= 0) && (depthY < depthHeight))
                                    {
                                        int depthIndex = (depthY * depthWidth) + depthX;

                                        // If we are tracking a body for the current pixel, do not zero out the pixel
                                        if (bodyIndexDataPointer[depthIndex] != 0xff)
                                        {
                                            continue;
                                        }
                                    }
                                }

                                bitmapPixelsPointer[colorIndex] = 0;
                            }
                        }

                        this._kinectColorBitmap.AddDirtyRect(new Int32Rect(0, 0, this._kinectColorBitmap.PixelWidth, this._kinectColorBitmap.PixelHeight));
                    }
                }
            }
            finally
            {
                if (isBitmapLocked)
                {
                    this._kinectColorBitmap.Unlock();
                }

                if (depthFrame != null)
                {
                    depthFrame.Dispose();
                }

                if (colorFrame != null)
                {
                    colorFrame.Dispose();
                }

                if (bodyIndexFrame != null)
                {
                    bodyIndexFrame.Dispose();
                }
            }
        }

        /// <summary>
        /// Handles the user invoking the screenshot command
        ///   Creates a .png file in Environment.SpecialFolder.MyPictures
        ///   with the contents of the ImageSource
        /// </summary>
        private void ScreenshotCommandExecute()
        {
            // Create a render target to which we'll render our composite image
            RenderTargetBitmap renderBitmap = new RenderTargetBitmap((int)_kinectColorBitmap.Width, (int)_kinectColorBitmap.Height, 96.0, 96.0, PixelFormats.Pbgra32);

            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                dc.DrawImage(ImageSource, new Rect(new Point(), new Size(_kinectColorBitmap.Width, _kinectColorBitmap.Height)));
            }

            renderBitmap.Render(dv);

            BitmapEncoder encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(renderBitmap));

            string time = System.DateTime.Now.ToString("hh'-'mm'-'ss", CultureInfo.CurrentUICulture.DateTimeFormat);

            string myPhotos = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

            string path = Path.Combine(myPhotos, "KinectScreenshot-" + time + ".png");

            // Write the new file to disk
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Create))
                {
                    encoder.Save(fs);
                }

                this.StatusText = string.Format(Properties.Resources.SavedScreenshotStatusTextFormat, path);
            }
            catch (IOException)
            {
                this.StatusText = string.Format(Properties.Resources.FailedScreenshotStatusTextFormat, path);
            }
        }

        private void RenderBodyFrame(BodyFrame bodyFrame)
        {
            bool dataReceived = false;

            Body[] bodies = null;
            if (bodyFrame != null)
            {
                if (bodies == null)
                {
                    bodies = new Body[bodyFrame.BodyCount];
                }

                // The first time GetAndRefreshBodyData is called, Kinect will allocate each Body in the array.
                // As long as those body objects are not disposed and not set to null in the array,
                // those body objects will be re-used.
                bodyFrame.GetAndRefreshBodyData(bodies);
                bodyFrame.Dispose();
                dataReceived = true;
            }

            if (dataReceived)
            {

                foreach (Body body in bodies)
                {

                    if (body.IsTracked)
                    {
                        TrackedHead = body.Joints[JointType.Head].Position;
                        //This is an 'aproxometery'  http://trailerpark.wikia.com/wiki/Rickyisms
                        //  of the tracking direction to be applied to the eyeballs on 
                        //  the screen.
                        TrackedHeadX = (int)(TrackedHead.X * 10);
                        TrackedHeadY = (int)(TrackedHead.Y * -10);

                        // Really, one should map the CameraSpacePoint to 
                        //  the angle between the location of the eyes on 
                        //  the physical screen and the tracked point. And stuff.                        //This is the TrackedHead Position (in Meters)
                        //The origin (x=0, y=0, z=0) is located at the center of the IR sensor on Kinect
                        //X grows to the sensor’s left
                        //Y grows up (note that this direction is based on the sensor’s tilt)
                        //Z grows out in the direction the sensor is facing
                        //1 unit = 1 meter

                        //Body
                        //body.Joints[JointType.Head].Position.X;
                        //body.Joints[JointType.Head].Position.Y;
                        //body.Joints[JointType.Head].Position.Z;

                        //Kinect (0,0,0)

                        //Screen Eyes (?,?,?)
                    }
                }

            }
        }

        #endregion


    }
}