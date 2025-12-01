using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using Microsoft.Win32;

namespace LightAddonApp;

public class ModelTreeNode
{
    public string Name { get; set; } = string.Empty;
    public Model3D? Model { get; set; }
    public ObservableCollection<ModelTreeNode> Children { get; } = new();
    public bool IsSelected { get; set; }
}

public enum LightType
{
    Directional,
    Point,
    Spot
}

public class LightSource
{
    public string Name { get; set; } = string.Empty;
    public LightType Type { get; set; }
    public Color Color { get; set; } = Colors.White;
    public double Intensity { get; set; } = 1.0;
    public Point3D Position { get; set; } = new Point3D(0, 0, 0);
    public Vector3D Direction { get; set; } = new Vector3D(0, -1, 0);
    public double SpotAngle { get; set; } = 45.0;
    public int RayCount { get; set; } = 10;
    public double RayLength { get; set; } = 100.0;
    public bool ShowRays { get; set; } = false;
    public Light? LightModel { get; set; }
    public ModelVisual3D? VisualContainer { get; set; }
}

public partial class MainWindow : Window
{
    private readonly StLReader _stlReader = new();
    private Model3D? _originalModel;
    private Model3DGroup? _modelWrapper;
    private readonly Dictionary<Model3D, Transform3DGroup> _partTransforms = new();
    private Model3D? _selectedModel;
    private Transform3DGroup? _wholeModelTransform;
    private bool _updatingControls;
    private ModelTreeNode? _rootTreeNode;
    private double _modelScale = 1.0;
    private readonly List<UIElement> _originalTransformPanelChildren = new();

    private readonly ObservableCollection<LightSource> _lightSources = new();
    private LightSource? _selectedLightSource;
    private ModelVisual3D? _lightsContainer;
    private ModelVisual3D? _raysContainer;
    private ModelVisual3D? _lightIndicatorsContainer;
    private bool _showRayVisualization = false;

    private ModelVisual3D? _gizmoContainer;
    private GeometryModel3D? _gizmoX, _gizmoY, _gizmoZ;
    private ModelVisual3D? _gizmoVisualX, _gizmoVisualY, _gizmoVisualZ;
    private bool _isDraggingGizmo = false;
    private char? _draggedAxis = null;
    private Point _lastMousePosition;
    private Point3D _gizmoDragStartPosition;
    private Vector3D _accumulatedGizmoOffset;
    private Point3D _gizmoStartPosition;
    private Point3D _initialModelCenter;
    private Vector3D _initialLightDirection = new Vector3D(0, -1, 0);
    
    private GeometryModel3D? _rotationGizmoX, _rotationGizmoY, _rotationGizmoZ;
    private ModelVisual3D? _rotationGizmoVisualX, _rotationGizmoVisualY, _rotationGizmoVisualZ;
    private bool _isRotatingGizmo = false;
    private char? _rotatedAxis = null;
    private double _accumulatedRotation = 0.0;

    private readonly Dictionary<Key, bool> _pressedKeys = new();
    private System.Windows.Threading.DispatcherTimer? _movementTimer;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text = "Готово. Откройте STL файл.";
        ResetCameraInternal();

        try
        {
            AllocConsole();
            Console.OutputEncoding = System.Text.Encoding.UTF8;

            System.Diagnostics.Trace.Listeners.Add(
                new System.Diagnostics.TextWriterTraceListener(Console.Out));
            System.Diagnostics.Trace.AutoFlush = true;

            Console.WriteLine("[DEBUG] Консоль отладки запущена");
            Console.WriteLine("[DEBUG] Debug.WriteLine будет виден в Visual Studio Output, Console.WriteLine - в этой консоли");
        }
        catch
        {
        }

        SetupCameraControls();

        Viewport.MouseLeftButtonDown += Viewport_MouseLeftButtonDown;
        Viewport.MouseMove += Viewport_MouseMove;
        Viewport.MouseLeftButtonUp += Viewport_MouseLeftButtonUp;

        KeyDown += MainWindow_KeyDown;
        KeyUp += MainWindow_KeyUp;
        Viewport.Focusable = true;
        Viewport.MouseEnter += (s, e) => Viewport.Focus();

        _movementTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16)
        };
        _movementTimer.Tick += MovementTimer_Tick;
        _movementTimer.Start();

        Viewport.Loaded += (s, e) => {
            InitializeLightSystem();
            SetupViewCube();
        };
        
        ContentRendered += (s, e) => {
            System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Render,
                new Action(() => {
                    SetupViewCubeLabels();
                }));
        };

        Loaded += (s, e) => {
            if (TransformPanel != null && _originalTransformPanelChildren.Count == 0)
            {
            }
        };
    }

    private void DebugLog(string message)
    {
        Console.WriteLine(message);
    }

    private void InitializeLightSystem()
    {
        _lightsContainer = new ModelVisual3D();
        _raysContainer = new ModelVisual3D();
        _lightIndicatorsContainer = new ModelVisual3D();
        _gizmoContainer = new ModelVisual3D();

        var children = Viewport.Children;
        var modelIndex = -1;

        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] == ModelContainer)
            {
                modelIndex = i;
                break;
            }
        }

        if (modelIndex >= 0)
        {
            children.Insert(modelIndex + 1, _lightsContainer);
            children.Insert(modelIndex + 2, _lightIndicatorsContainer);
            children.Insert(modelIndex + 3, _raysContainer);
            children.Insert(modelIndex + 4, _gizmoContainer);
        }
        else
        {
            children.Add(_lightsContainer);
            children.Add(_lightIndicatorsContainer);
            children.Add(_raysContainer);
            children.Add(_gizmoContainer);
        }

        _lightSources.CollectionChanged += (s, e) => {
            UpdateLightsInScene();
            UpdateLightIndicators();
        };

        if (LightsListBox != null)
        {
            LightsListBox.ItemsSource = _lightSources;
        }

        InitializeGizmos();
    }

    private void SetupViewCube()
    {
        if (Viewport.Camera is PerspectiveCamera cam)
        {
            cam.UpDirection = new Vector3D(0, 1, 0);
        }
    }

    private void SetupViewCubeLabels()
    {
        try
        {
            var viewCubeVisual = FindViewCubeVisualInViewport();
            if (viewCubeVisual != null)
            {
                Console.WriteLine($"Found ViewCubeVisual: {viewCubeVisual.GetType().FullName}");
                
                var viewCubeType = viewCubeVisual.GetType();
                
                var labelMap = new Dictionary<string, string>
                {
                    { "FrontText", "+Y" },
                    { "BackText", "-Y" },
                    { "LeftText", "-X" },
                    { "RightText", "+X" },
                    { "TopText", "+Z" },
                    { "BottomText", "-Z" }
                };
                
                foreach (var kvp in labelMap)
                {
                    var prop = viewCubeType.GetProperty(kvp.Key);
                    if (prop != null && prop.CanWrite)
                    {
                        prop.SetValue(viewCubeVisual, kvp.Value);
                        Console.WriteLine($"Set {kvp.Key} = {kvp.Value}");
                    }
                    else
                    {
                        var field = viewCubeType.GetField(kvp.Key, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
                        if (field != null)
                        {
                            field.SetValue(viewCubeVisual, kvp.Value);
                            Console.WriteLine($"Set field {kvp.Key} = {kvp.Value}");
                        }
                    }
                }
            }
            else
            {
                Console.WriteLine("ViewCube not found in viewport - trying alternative search");
                TryFindViewCubeAlternative();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Не удалось изменить метки ViewCube: {ex.Message}\n{ex.StackTrace}");
        }
    }
    
    private void TryFindViewCubeAlternative()
    {
        try
        {
            var viewportType = Viewport.GetType();
            var allFields = viewportType.GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            foreach (var field in allFields)
            {
                if (field.Name.ToLower().Contains("viewcube"))
                {
                    Console.WriteLine($"Found ViewCube field: {field.Name}");
                    var viewCube = field.GetValue(Viewport);
                    if (viewCube != null)
                    {
                        SetupViewCubeLabelsOnObject(viewCube);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Alternative search failed: {ex.Message}");
        }
    }
    
    private void SetupViewCubeLabelsOnObject(object viewCube)
    {
        try
        {
            var viewCubeType = viewCube.GetType();
            
            var labelMap = new Dictionary<string, string>
            {
                { "FrontText", "+Y" },
                { "BackText", "-Y" },
                { "LeftText", "-X" },
                { "RightText", "+X" },
                { "TopText", "+Z" },
                { "BottomText", "-Z" }
            };
            
            foreach (var kvp in labelMap)
            {
                var prop = viewCubeType.GetProperty(kvp.Key);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(viewCube, kvp.Value);
                    Console.WriteLine($"Set {kvp.Key} = {kvp.Value}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to set labels on ViewCube object: {ex.Message}");
        }
    }
    
    private object? FindViewCubeVisualInViewport()
    {
        try
        {
            var viewportType = Viewport.GetType();
            var viewCubeField = viewportType.GetField("_viewCube", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (viewCubeField != null)
            {
                return viewCubeField.GetValue(Viewport);
            }
            
            var viewCubeProp = viewportType.GetProperty("ViewCube", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public);
            if (viewCubeProp != null)
            {
                return viewCubeProp.GetValue(Viewport);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error accessing ViewCube property: {ex.Message}");
        }
        
        return FindViewCubeInVisualTree(Viewport);
    }

    private object? FindViewCubeInVisualTree(DependencyObject parent)
    {
        if (parent == null) return null;
        
        var parentType = parent.GetType();
        if (parentType.Name == "ViewCube" || parentType.FullName?.Contains("ViewCube") == true)
        {
            return parent;
        }
        
        var childrenCount = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < childrenCount; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            var result = FindViewCubeInVisualTree(child);
            if (result != null)
            {
                return result;
            }
        }
        
        if (parent is FrameworkElement fe)
        {
            foreach (var logicalChild in LogicalTreeHelper.GetChildren(fe))
            {
                if (logicalChild is DependencyObject depObj)
                {
                    var result = FindViewCubeInVisualTree(depObj);
                    if (result != null)
                    {
                        return result;
                    }
                }
            }
        }
        
        return null;
    }

    private T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t)
            {
                return t;
            }
            var childOfChild = FindVisualChild<T>(child);
            if (childOfChild != null)
            {
                return childOfChild;
            }
        }
        return null;
    }

    private void OpenStl_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "STL models (*.stl)|*.stl",
            Title = "Выберите STL модель"
        };

        if (dialog.ShowDialog() == true)
        {
            LoadStl(dialog.FileName);
        }
    }

    private void LoadStl(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                StatusText.Text = "Файл не найден.";
                return;
            }

            StatusText.Text = "Загрузка модели...";

            var model = _stlReader.Read(path);
            if (model == null)
            {
                StatusText.Text = "Ошибка: не удалось загрузить модель.";
                return;
            }

            _originalModel = model;
            ApplyFlatMaterial(model);

            SetupModelTransforms(model);

            BuildModelTree(model, Path.GetFileName(path));

            _selectedModel = _originalModel;
            _selectedLightSource = null;
            if (LightsListBox != null) LightsListBox.SelectedItem = null;

            _modelScale = CalculateModelScale(model);

            ResetCameraInternal();
            SetupCameraControls();

            UpdateVisualSelection();
            ShowModelSettingsPanel();

            StatusText.Text = $"Загружено: {Path.GetFileName(path)}";
        }
        catch (System.Exception ex)
        {
            StatusText.Text = "Ошибка загрузки STL. Подробности в окне сообщения.";
            MessageBox.Show(this,
                ex.Message,
                "Ошибка загрузки STL",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void BuildModelTree(Model3D model, string modelName)
    {
        ModelTreeView.Items.Clear();
        _rootTreeNode = new ModelTreeNode { Name = modelName, Model = model };

        var rootItem = new TreeViewItem
        {
            Header = modelName,
            Tag = _rootTreeNode,
            IsExpanded = true
        };
        rootItem.MouseLeftButtonDown += (s, e) => { SelectModelNode(_rootTreeNode); e.Handled = true; };

        if (model is Model3DGroup group)
        {
            var partIndex = 0;
            foreach (var child in group.Children)
            {
                var childNode = new ModelTreeNode 
                { 
                    Name = $"Часть {partIndex + 1}", 
                    Model = child 
                };
                _rootTreeNode.Children.Add(childNode);

                var childItem = new TreeViewItem
                {
                    Header = childNode.Name,
                    Tag = childNode
                };
                childItem.MouseLeftButtonDown += (s, e) => { SelectModelNode(childNode); e.Handled = true; };

                if (child is Model3DGroup childGroup)
                {
                    var geomIndex = 0;
                    foreach (var geom in childGroup.Children.OfType<GeometryModel3D>())
                    {
                        var geomNode = new ModelTreeNode 
                        { 
                            Name = $"Геометрия {geomIndex + 1}", 
                            Model = geom 
                        };
                        childNode.Children.Add(geomNode);

                        var geomItem = new TreeViewItem
                        {
                            Header = geomNode.Name,
                            Tag = geomNode
                        };
                        geomItem.MouseLeftButtonDown += (s, e) => { SelectModelNode(geomNode); e.Handled = true; };

                        childItem.Items.Add(geomItem);
                        geomIndex++;
                    }
                }
                else if (child is GeometryModel3D gm)
                {
                    if (gm.Geometry is MeshGeometry3D mesh)
                    {
                        var faceCount = mesh.TriangleIndices.Count / 3;
                        var faceNode = new ModelTreeNode 
                        { 
                            Name = $"Грани ({faceCount})", 
                            Model = gm 
                        };
                        childNode.Children.Add(faceNode);

                        var faceItem = new TreeViewItem
                        {
                            Header = faceNode.Name,
                            Tag = faceNode
                        };
                        faceItem.MouseLeftButtonDown += (s, e) => { SelectModelNode(faceNode); e.Handled = true; };

                        childItem.Items.Add(faceItem);
                    }
                }

                rootItem.Items.Add(childItem);
                partIndex++;
            }
        }

        ModelTreeView.Items.Add(rootItem);
    }

    private void SelectModelNode(ModelTreeNode? node)
    {
        try
        {
            Console.WriteLine($"[DEBUG] SelectModelNode: начало, node={node?.Name ?? "null"}");

            if (node == null)
            {
                Console.WriteLine("[DEBUG] SelectModelNode: node == null, возврат");
                return;
            }

            if (node.Model == null)
            {
                Console.WriteLine($"[DEBUG] SelectModelNode: node.Model == null для узла {node.Name}");
                StatusText.Text = $"Предупреждение: модель для '{node.Name}' недоступна";
                return;
            }

            Console.WriteLine($"[DEBUG] SelectModelNode: node.Model={node.Model.GetHashCode()}, тип={node.Model.GetType().Name}");

            if (node.Model is GeometryModel3D && _originalModel is Model3DGroup originalGroup)
            {
                var parentGroup = FindParentGroup(node.Model, originalGroup);
                if (parentGroup != null && _partTransforms.ContainsKey(parentGroup))
                {
                    Console.WriteLine($"[DEBUG] SelectModelNode: найдена родительская группа {parentGroup.GetHashCode()} для грани");
                    _selectedModel = parentGroup;
                }
                else
                {
                    Console.WriteLine($"[DEBUG] SelectModelNode: родительская группа не найдена или нет трансформации, используем саму геометрию");
                    _selectedModel = node.Model;
                }
            }
            else
            {
                Console.WriteLine($"[DEBUG] SelectModelNode: установка _selectedModel={node.Model.GetHashCode()}");
                _selectedModel = node.Model;
            }
            _selectedLightSource = null;

            if (LightsListBox != null)
            {
                LightsListBox.SelectedItem = null;
            }

            Console.WriteLine("[DEBUG] SelectModelNode: вызов UpdateVisualSelection");
            UpdateVisualSelection();

            Console.WriteLine("[DEBUG] SelectModelNode: вызов UpdateLightSettingsPanel (который вызовет ShowModelSettingsPanel)");
            UpdateLightSettingsPanel();

            Console.WriteLine("[DEBUG] SelectModelNode: завершено успешно");
            StatusText.Text = $"Выбрано: {node.Name}";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] ОШИБКА SelectModelNode: {ex.Message}\n{ex.StackTrace}");
            StatusText.Text = $"Ошибка выбора: {ex.Message}";
        }
    }

    private void ModelTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is TreeViewItem item && item.Tag is ModelTreeNode node)
        {
            SelectModelNode(node);
        }
    }

    private void SetupModelTransforms(Model3D model)
    {
        _partTransforms.Clear();

        _wholeModelTransform = new Transform3DGroup();
        _wholeModelTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0), new Point3D(0, 0, 0)));
        _wholeModelTransform.Children.Add(new TranslateTransform3D(0, 0, 0));

        if (model is Model3DGroup group)
        {
            _modelWrapper = new Model3DGroup { Transform = _wholeModelTransform };

            if (group.Children.Count > 1)
            {
                foreach (var child in group.Children)
                {
                    var partTransform = new Transform3DGroup();
                    partTransform.Children.Add(new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0), new Point3D(0, 0, 0)));
                    partTransform.Children.Add(new TranslateTransform3D(0, 0, 0));

                    var partWrapper = new Model3DGroup { Transform = partTransform };
                    partWrapper.Children.Add(child);

                    _partTransforms[child] = partTransform;
                    _modelWrapper.Children.Add(partWrapper);
                }
            }
            else
            {
                _modelWrapper.Children.Add(group);
            }
        }
        else
        {
            _modelWrapper = new Model3DGroup { Transform = _wholeModelTransform };
            _modelWrapper.Children.Add(model);
        }

        ModelContainer.Content = _modelWrapper;
    }

    private void ResetCamera_Click(object sender, RoutedEventArgs e)
    {
        ResetCameraInternal();
        StatusText.Text = "Камера сброшена.";
    }

    private void ResetCameraInternal()
    {
        if (Viewport.Camera is PerspectiveCamera cam)
        {
            cam.Position = new Point3D(10, 10, 10);
            cam.LookDirection = new Vector3D(-10, -10, -10);
            cam.UpDirection = new Vector3D(0, 1, 0);
        }

        Viewport.ZoomExtents(0.4);
    }

    private void SetupCameraControls()
    {
        if (Viewport.CameraController != null)
        {
            Viewport.CameraController.LeftRightRotationSensitivity = 1.0;
            Viewport.CameraController.UpDownRotationSensitivity = 1.0;

            Viewport.CameraController.ZoomSensitivity = Math.Max(0.005, 0.005 * _modelScale);

            Viewport.CameraController.RotateAroundMouseDownPoint = false;

            Viewport.CameraController.InertiaFactor = 0.85;
        }
    }

    private double CalculateModelScale(Model3D model)
    {
        var bounds = CalculateBounds(model);
        if (bounds.HasValue)
        {
            var size = bounds.Value.Size;
            var maxSize = Math.Max(Math.Max(size.X, size.Y), size.Z);

            return Math.Max(0.1, maxSize / 10.0);
        }

        return 1.0;
    }

    private Rect3D? CalculateBounds(Model3D model)
    {
        if (model is GeometryModel3D gm)
        {
            return gm.Bounds;
        }
        else if (model is Model3DGroup group)
        {
            Rect3D? totalBounds = null;
            foreach (var child in group.Children)
            {
                var childBounds = CalculateBounds(child);
                if (childBounds.HasValue)
                {
                    if (totalBounds.HasValue)
                    {
                        totalBounds = Rect3D.Union(totalBounds.Value, childBounds.Value);
                    }
                    else
                    {
                        totalBounds = childBounds;
                    }
                }
            }
            return totalBounds;
        }

        return model.Bounds;
    }

    private void MainWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.W || e.Key == Key.A || e.Key == Key.S || e.Key == Key.D || 
            e.Key == Key.Q || e.Key == Key.E)
        {
            _pressedKeys[e.Key] = true;
            e.Handled = true;
        }
    }

    private void MainWindow_KeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.W || e.Key == Key.A || e.Key == Key.S || e.Key == Key.D || 
            e.Key == Key.Q || e.Key == Key.E)
        {
            _pressedKeys[e.Key] = false;
            e.Handled = true;
        }
    }

    private void MovementTimer_Tick(object? sender, EventArgs e)
    {
        if (Viewport.Camera is not PerspectiveCamera cam || Viewport.CameraController == null)
            return;

        bool hasMovement = _pressedKeys.ContainsKey(Key.W) && _pressedKeys[Key.W] ||
                          _pressedKeys.ContainsKey(Key.A) && _pressedKeys[Key.A] ||
                          _pressedKeys.ContainsKey(Key.S) && _pressedKeys[Key.S] ||
                          _pressedKeys.ContainsKey(Key.D) && _pressedKeys[Key.D] ||
                          _pressedKeys.ContainsKey(Key.Q) && _pressedKeys[Key.Q] ||
                          _pressedKeys.ContainsKey(Key.E) && _pressedKeys[Key.E];

        if (!hasMovement) return;

        var moveSpeed = 0.1 * _modelScale;
        var lookDir = cam.LookDirection;
        var upDir = cam.UpDirection;
        var rightDir = Vector3D.CrossProduct(lookDir, upDir);
        rightDir.Normalize();

        var normalizedLook = lookDir;
        normalizedLook.Normalize();
        var normalizedUp = upDir;
        normalizedUp.Normalize();

        var forward = normalizedLook;
        var right = rightDir;
        var up = normalizedUp;

        forward = forward - Vector3D.Multiply(Vector3D.DotProduct(forward, up), up);
        forward.Normalize();

        Vector3D moveDirection = new Vector3D(0, 0, 0);

        if (_pressedKeys.ContainsKey(Key.W) && _pressedKeys[Key.W])
        {
            moveDirection += forward * moveSpeed;
        }
        if (_pressedKeys.ContainsKey(Key.S) && _pressedKeys[Key.S])
        {
            moveDirection -= forward * moveSpeed;
        }
        if (_pressedKeys.ContainsKey(Key.A) && _pressedKeys[Key.A])
        {
            moveDirection -= right * moveSpeed;
        }
        if (_pressedKeys.ContainsKey(Key.D) && _pressedKeys[Key.D])
        {
            moveDirection += right * moveSpeed;
        }
        if (_pressedKeys.ContainsKey(Key.Q) && _pressedKeys[Key.Q])
        {
            moveDirection -= up * moveSpeed;
        }
        if (_pressedKeys.ContainsKey(Key.E) && _pressedKeys[Key.E])
        {
            moveDirection += up * moveSpeed;
        }

        if (moveDirection.Length > 0)
        {
            cam.Position += moveDirection;
        }
    }

    private static void ApplyFlatMaterial(Model3D model)
    {
        if (model is GeometryModel3D gm)
        {
            var baseColor = Color.FromRgb(0x4C, 0xAF, 0x50);
            var diffuseBrush = new SolidColorBrush(baseColor);
            diffuseBrush.Freeze();

            var matGroup = new MaterialGroup();
            matGroup.Children.Add(new DiffuseMaterial(diffuseBrush));
            matGroup.Children.Add(new SpecularMaterial(
                new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 20));

            gm.Material = matGroup;
            gm.BackMaterial = matGroup;
        }
        else if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                ApplyFlatMaterial(child);
            }
        }
    }

    private void Viewport_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            var hits = Viewport.Viewport.FindHits(e.GetPosition(Viewport));
            bool hitModel = false;
            bool hitGizmo = false;
            bool hitLightIndicator = false;

            foreach (var hit in hits)
            {
                if (hit.Model != null)
                {
                    if (IsGizmoHit(hit))
                    {
                        hitGizmo = true;
                        if ((_selectedModel != null || _selectedLightSource != null) && _gizmoContainer != null)
                        {
                            if (IsRotationGizmoHit(hit))
                            {
                                var rotAxis = GetRotationAxisFromHit(hit);
                                if (rotAxis.HasValue)
                                {
                                    _isRotatingGizmo = true;
                                    _rotatedAxis = rotAxis.Value;
                                    _accumulatedRotation = 0.0;
                                    if (_selectedLightSource != null && (_selectedLightSource.Type == LightType.Directional || _selectedLightSource.Type == LightType.Spot))
                                    {
                                        _initialLightDirection = _selectedLightSource.Direction;
                                        _initialLightDirection.Normalize();
                                    }
                                    _lastMousePosition = e.GetPosition(Viewport);
                                    Console.WriteLine($"[DEBUG] MouseLeftButtonDown: начало вращения по оси {rotAxis.Value}");
                                    e.Handled = true;
                                    Viewport.CaptureMouse();
                                    return;
                                }
                            }
                            else
                            {
                                var axis = GetGizmoAxisFromHit(hit);
                                if (axis.HasValue)
                                {
                                    _isDraggingGizmo = true;
                                    _draggedAxis = axis.Value;
                                    _lastMousePosition = e.GetPosition(Viewport);
                                    var posVec = GetCurrentPosition();
                                    _gizmoDragStartPosition = new Point3D(posVec.X, posVec.Y, posVec.Z);
                                    _accumulatedGizmoOffset = new Vector3D(0, 0, 0);
                                    Console.WriteLine($"[DEBUG] MouseLeftButtonDown: начало перетаскивания, _gizmoDragStartPosition=({_gizmoDragStartPosition.X:F2}, {_gizmoDragStartPosition.Y:F2}, {_gizmoDragStartPosition.Z:F2})");
                                    e.Handled = true;
                                    Viewport.CaptureMouse();
                                    return;
                                }
                            }
                        }
                        continue;
                    }

                    if (_modelWrapper != null && IsModelInWrapper(hit.Model))
                    {
                        hitModel = true;
                    }
                }

                if (hit.Visual != null)
                {
                    var visual = hit.Visual as ModelVisual3D;
                    if (visual != null && IsVisualInLightIndicatorsContainer(visual))
                    {
                        hitLightIndicator = true;
                        var tag = visual.GetValue(System.Windows.FrameworkElement.TagProperty);
                        if (tag is LightSource lightSource)
                        {
                            _selectedLightSource = lightSource;
                            _selectedModel = null;
                            if (LightsListBox != null) LightsListBox.SelectedItem = lightSource;

                            Console.WriteLine($"[DEBUG] Выбран источник света: {lightSource.Name}");
                            UpdateLightSettingsPanel();

                            e.Handled = true;
                            return;
                        }
                        continue;
                    }
                }
            }

            if (_modelWrapper == null || _originalModel == null)
            {
                ClearSelection();
                e.Handled = true;
                return;
            }

            if (!hitModel && !hitGizmo && !hitLightIndicator)
            {
                ClearSelection();
                e.Handled = true;
                return;
            }

            if (!hitModel)
            {
                return;
            }

            if (_originalModel is Model3DGroup group && group.Children.Count > 1)
            {
                var parts = group.Children.ToList();
                Model3D? nextPart = null;

                if (parts.Count == 0)
                {
                    StatusText.Text = "Модель не содержит частей";
                    return;
                }

                if (_selectedModel == null || ReferenceEquals(_selectedModel, _originalModel))
                {
                    nextPart = parts[0];
                }
                else
                {
                    var currentIndex = -1;
                    for (int i = 0; i < parts.Count; i++)
                    {
                        if (ReferenceEquals(parts[i], _selectedModel))
                        {
                            currentIndex = i;
                            break;
                        }
                    }

                    if (currentIndex >= 0 && currentIndex < parts.Count - 1)
                    {
                        nextPart = parts[currentIndex + 1];
                    }
                    else
                    {
                        nextPart = _originalModel;
                    }
                }

                if (nextPart == null)
                {
                    nextPart = _originalModel;
                }

                _selectedModel = nextPart;
                _selectedLightSource = null;
                if (LightsListBox != null) LightsListBox.SelectedItem = null;

                Console.WriteLine($"[DEBUG] Выбор модели: selectedModel={_selectedModel?.GetHashCode()}, originalModel={_originalModel?.GetHashCode()}");

                UpdateVisualSelection();

                try
                {
                    Console.WriteLine("[DEBUG] Вызов UpdateLightSettingsPanel после выбора модели");
                    UpdateLightSettingsPanel();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG] ОШИБКА при обновлении панели/gizmos: {ex.Message}\n{ex.StackTrace}");
                    StatusText.Text = $"Ошибка обновления панели: {ex.Message}";
                }

                if (_rootTreeNode != null)
                {
                    var node = FindTreeNode(_rootTreeNode, _selectedModel);
                    if (node != null)
                    {
                        StatusText.Text = $"Выбрано: {node.Name}";
                        return;
                    }
                }

                StatusText.Text = ReferenceEquals(_selectedModel, _originalModel) 
                    ? "Выбрана вся модель" 
                    : "Выбрана часть модели";
            }
            else
            {
                _selectedModel = _originalModel;
                _selectedLightSource = null;
                if (LightsListBox != null) LightsListBox.SelectedItem = null;

                Console.WriteLine($"[DEBUG] Выбор всей модели: selectedModel={_selectedModel?.GetHashCode()}, originalModel={_originalModel?.GetHashCode()}");

                UpdateVisualSelection();

                try
                {
                    Console.WriteLine("[DEBUG] Вызов UpdateLightSettingsPanel после выбора всей модели");
                    UpdateLightSettingsPanel();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DEBUG] ОШИБКА при обновлении панели/gizmos: {ex.Message}\n{ex.StackTrace}");
                    StatusText.Text = $"Ошибка обновления панели: {ex.Message}";
                }

                StatusText.Text = "Выбрана вся модель.";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] ОШИБКА Viewport_MouseLeftButtonDown: {ex.Message}\n{ex.StackTrace}");
            StatusText.Text = $"Ошибка при выборе модели: {ex.Message}";
            MessageBox.Show(this, $"Ошибка при выборе модели:\n{ex.Message}\n\n{ex.StackTrace}", 
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_isRotatingGizmo && _rotatedAxis.HasValue && (_selectedModel != null || _selectedLightSource != null))
        {
            HandleRotationGizmoDrag(e);
            return;
        }
        
        if (!_isDraggingGizmo || !_draggedAxis.HasValue || (_selectedModel == null && _selectedLightSource == null)) return;

        try
        {
            var currentMousePos = e.GetPosition(Viewport);
            var delta = currentMousePos - _lastMousePosition;

            if (Viewport.Camera is not PerspectiveCamera cam) return;

            Vector3D axisDirection = _draggedAxis.Value switch
            {
                'X' => new Vector3D(1, 0, 0),
                'Y' => new Vector3D(0, 1, 0),
                'Z' => new Vector3D(0, 0, 1),
                _ => new Vector3D(0, 0, 0)
            };

            var lookDir = cam.LookDirection;
            lookDir.Normalize();
            var upDir = cam.UpDirection;
            upDir.Normalize();
            var rightDir = Vector3D.CrossProduct(lookDir, upDir);
            rightDir.Normalize();

            var axisScreenX = Vector3D.DotProduct(axisDirection, rightDir);
            var axisScreenY = Vector3D.DotProduct(axisDirection, upDir);

            var mouseDeltaX = delta.X;
            var mouseDeltaY = -delta.Y;

            var movementAlongAxis = mouseDeltaX * axisScreenX + mouseDeltaY * axisScreenY;

            double scaleFactor = 0.005;
            if (_selectedLightSource != null)
            {
                var distanceToObject = (cam.Position - _selectedLightSource.Position).Length;
                scaleFactor = Math.Max(0.0005, distanceToObject * 0.00005);
            }
            else
            {
                var bounds = GetSelectedModelBounds();
                if (bounds.HasValue)
                {
                    var maxSize = Math.Max(Math.Max(bounds.Value.SizeX, bounds.Value.SizeY), bounds.Value.SizeZ);
                    var distanceToObject = (cam.Position - new Point3D(
                        bounds.Value.X + bounds.Value.SizeX / 2,
                        bounds.Value.Y + bounds.Value.SizeY / 2,
                        bounds.Value.Z + bounds.Value.SizeZ / 2
                    )).Length;
                    scaleFactor = Math.Max(0.0005, distanceToObject * 0.00005 * (maxSize / 10.0));
                }
            }

            var movement = axisDirection * movementAlongAxis * scaleFactor;

            _accumulatedGizmoOffset = new Vector3D(
                _accumulatedGizmoOffset.X + movement.X,
                _accumulatedGizmoOffset.Y + movement.Y,
                _accumulatedGizmoOffset.Z + movement.Z
            );

            var newPos = new Point3D(
                _gizmoDragStartPosition.X + _accumulatedGizmoOffset.X,
                _gizmoDragStartPosition.Y + _accumulatedGizmoOffset.Y,
                _gizmoDragStartPosition.Z + _accumulatedGizmoOffset.Z
            );

            Console.WriteLine($"[DEBUG] MouseMove: _gizmoDragStartPosition=({_gizmoDragStartPosition.X:F2}, {_gizmoDragStartPosition.Y:F2}, {_gizmoDragStartPosition.Z:F2}), movement=({movement.X:F4}, {movement.Y:F4}, {movement.Z:F4}), _accumulatedGizmoOffset=({_accumulatedGizmoOffset.X:F4}, {_accumulatedGizmoOffset.Y:F4}, {_accumulatedGizmoOffset.Z:F4}), newPos=({newPos.X:F2}, {newPos.Y:F2}, {newPos.Z:F2})");

            SetPosition(newPos.X, newPos.Y, newPos.Z);

            Point3D gizmoPos;
            if (_selectedLightSource != null)
            {
                gizmoPos = newPos;
            }
            else
            {
                gizmoPos = new Point3D(_initialModelCenter.X + newPos.X, _initialModelCenter.Y + newPos.Y, _initialModelCenter.Z + newPos.Z);
            }
            Console.WriteLine($"[DEBUG] MouseMove: _initialModelCenter=({_initialModelCenter.X:F2}, {_initialModelCenter.Y:F2}, {_initialModelCenter.Z:F2}), newPos=({newPos.X:F2}, {newPos.Y:F2}, {newPos.Z:F2}), gizmoPos=({gizmoPos.X:F2}, {gizmoPos.Y:F2}, {gizmoPos.Z:F2})");
            UpdateGizmoPosition(gizmoPos);

            _updatingControls = true;
            try
            {
                if (_selectedLightSource != null)
                {
                    if (TransformPanel != null)
                    {
                        var children = TransformPanel.Children;
                        TextBox? posXText = null, posYText = null, posZText = null;

                        for (int i = 0; i < children.Count; i++)
                        {
                            if (children[i] is TextBlock label)
                            {
                                if (label.Text == "Позиция X:" && i + 1 < children.Count && children[i + 1] is TextBox xBox)
                                    posXText = xBox;
                                else if (label.Text == "Позиция Y:" && i + 1 < children.Count && children[i + 1] is TextBox yBox)
                                    posYText = yBox;
                                else if (label.Text == "Позиция Z:" && i + 1 < children.Count && children[i + 1] is TextBox zBox)
                                    posZText = zBox;
                            }
                        }

                        if (posXText != null) posXText.Text = newPos.X.ToString("F2");
                        if (posYText != null) posYText.Text = newPos.Y.ToString("F2");
                        if (posZText != null) posZText.Text = newPos.Z.ToString("F2");
                    }
                }
                else
                {
                    var posXText = FindName("PositionXText") as TextBox;
                    var posYText = FindName("PositionYText") as TextBox;
                    var posZText = FindName("PositionZText") as TextBox;

                    if (posXText != null) posXText.Text = newPos.X.ToString("F2");
                    if (posYText != null) posYText.Text = newPos.Y.ToString("F2");
                    if (posZText != null) posZText.Text = newPos.Z.ToString("F2");
                }
            }
            finally
            {
                _updatingControls = false;
            }

            _lastMousePosition = currentMousePos;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при перетаскивании gizmo: {ex.Message}");
        }
    }

    private void HandleRotationGizmoDrag(MouseEventArgs e)
    {
        if (!_isRotatingGizmo || !_rotatedAxis.HasValue || (_selectedModel == null && _selectedLightSource == null)) return;

        try
        {
            var currentMousePos = e.GetPosition(Viewport);
            var delta = currentMousePos - _lastMousePosition;

            var rotationSpeed = 0.5;
            var angle = (delta.X + delta.Y) * rotationSpeed;
            _accumulatedRotation += angle;

            if (_selectedLightSource != null)
            {
                ApplyLightRotation(_rotatedAxis.Value, _accumulatedRotation);
            }
            else
            {
                ApplyRotation(_rotatedAxis.Value, _accumulatedRotation);
            }

            _lastMousePosition = currentMousePos;
            e.Handled = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка при вращении gizmo: {ex.Message}");
        }
    }

    private void ApplyRotation(char axis, double angle)
    {
        if (_selectedModel == null) return;

        Vector3D rotationAxis = axis switch
        {
            'X' => new Vector3D(1, 0, 0),
            'Y' => new Vector3D(0, 1, 0),
            'Z' => new Vector3D(0, 0, 1),
            _ => new Vector3D(0, 1, 0)
        };

        var rotationPoint = _initialModelCenter;

        Transform3DGroup? targetTransform = null;

        if (ReferenceEquals(_selectedModel, _originalModel) && _wholeModelTransform != null)
        {
            targetTransform = _wholeModelTransform;
        }
        else if (_partTransforms.ContainsKey(_selectedModel))
        {
            targetTransform = _partTransforms[_selectedModel];
        }

        if (targetTransform != null)
        {
            var rotation = targetTransform.Children.OfType<RotateTransform3D>().FirstOrDefault();
            if (rotation != null && rotation.Rotation is AxisAngleRotation3D axisAngle)
            {
                if (Math.Abs(Vector3D.DotProduct(axisAngle.Axis, rotationAxis) - 1.0) < 0.01)
                {
                    axisAngle.Angle = angle;
                }
                else
                {
                    axisAngle.Axis = rotationAxis;
                    axisAngle.Angle = angle;
                }
                rotation.CenterX = rotationPoint.X;
                rotation.CenterY = rotationPoint.Y;
                rotation.CenterZ = rotationPoint.Z;
            }
        }
    }

    private void ApplyLightRotation(char axis, double angle)
    {
        if (_selectedLightSource == null) return;

        if (_selectedLightSource.Type == LightType.Point)
        {
            return;
        }

        Vector3D rotationAxis = axis switch
        {
            'X' => new Vector3D(1, 0, 0),
            'Y' => new Vector3D(0, 1, 0),
            'Z' => new Vector3D(0, 0, 1),
            _ => new Vector3D(0, 1, 0)
        };

        var initialDir = _initialLightDirection;
        if (initialDir.Length < 0.01)
        {
            initialDir = _selectedLightSource.Direction;
            initialDir.Normalize();
        }

        var angleRad = angle * Math.PI / 180.0;
        
        var cosAngle = Math.Cos(angleRad);
        var sinAngle = Math.Sin(angleRad);

        var dot = Vector3D.DotProduct(rotationAxis, initialDir);
        var cross = Vector3D.CrossProduct(rotationAxis, initialDir);

        var rotatedDir = initialDir * cosAngle + cross * sinAngle + rotationAxis * dot * (1 - cosAngle);
        rotatedDir.Normalize();

        _selectedLightSource.Direction = rotatedDir;
        UpdateLightDirection(_selectedLightSource);
    }

    private void Viewport_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_isDraggingGizmo)
        {
            _isDraggingGizmo = false;
            _draggedAxis = null;
            Viewport.ReleaseMouseCapture();
            UpdateTransformControls();
            e.Handled = true;
        }
        
        if (_isRotatingGizmo)
        {
            _isRotatingGizmo = false;
            _rotatedAxis = null;
            _accumulatedRotation = 0.0;
            Viewport.ReleaseMouseCapture();
            UpdateTransformControls();
            e.Handled = true;
        }
    }

    private bool IsGizmoHit(HelixToolkit.Wpf.PointHitResult hit)
    {
        if (_gizmoContainer == null) return false;

        if (hit.Model != null)
        {
            if (hit.Model == _gizmoX || hit.Model == _gizmoY || hit.Model == _gizmoZ ||
                hit.Model == _rotationGizmoX || hit.Model == _rotationGizmoY || hit.Model == _rotationGizmoZ)
            {
                return true;
            }
        }

        return false;
    }
    
    private bool IsRotationGizmoHit(HelixToolkit.Wpf.PointHitResult hit)
    {
        if (hit.Model != null)
        {
            if (hit.Model == _rotationGizmoX || hit.Model == _rotationGizmoY || hit.Model == _rotationGizmoZ)
            {
                return true;
            }
        }
        return false;
    }

    private bool IsVisualInLightIndicatorsContainer(ModelVisual3D visual)
    {
        if (_lightIndicatorsContainer == null || visual == null) return false;

        foreach (var child in _lightIndicatorsContainer.Children)
        {
            if (ReferenceEquals(child, visual)) return true;
        }

        return false;
    }

    private bool IsModelInWrapper(Model3D model)
    {
        if (_modelWrapper == null || model == null) return false;
        return IsPartOfModel(model, _modelWrapper);
    }

    private char? GetGizmoAxisFromHit(HelixToolkit.Wpf.PointHitResult hit)
    {
        if (hit.Model != null && hit.Model is GeometryModel3D geomModel)
        {
            if (geomModel == _gizmoX) return 'X';
            if (geomModel == _gizmoY) return 'Y';
            if (geomModel == _gizmoZ) return 'Z';

            var tag = geomModel.GetValue(System.Windows.FrameworkElement.TagProperty);
            if (tag is char axis && (axis == 'X' || axis == 'Y' || axis == 'Z'))
            {
                return axis;
            }
        }

        return null;
    }
    
    private char? GetRotationAxisFromHit(HelixToolkit.Wpf.PointHitResult hit)
    {
        if (hit.Model != null && hit.Model is GeometryModel3D geomModel)
        {
            if (geomModel == _rotationGizmoX) return 'X';
            if (geomModel == _rotationGizmoY) return 'Y';
            if (geomModel == _rotationGizmoZ) return 'Z';
        }
        return null;
    }

    private Vector3D GetScreenProjection(Vector3D worldDirection, PerspectiveCamera cam, Vector3D rightDir, Vector3D upDir)
    {
        var projectionX = Vector3D.DotProduct(worldDirection, rightDir);
        var projectionY = Vector3D.DotProduct(worldDirection, upDir);
        return new Vector3D(projectionX, projectionY, 0);
    }

    private ModelTreeNode? FindTreeNode(ModelTreeNode node, Model3D? model)
    {
        if (node.Model == model) return node;

        foreach (var child in node.Children)
        {
            var found = FindTreeNode(child, model);
            if (found != null) return found;
        }

        return null;
    }

    private bool IsPartOfModel(Model3D? part, Model3D model)
    {
        if (part == null || model == null) return false;
        if (ReferenceEquals(part, model)) return true;

        if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                if (IsPartOfModel(part, child)) return true;
            }
        }

        return false;
    }

    private Model3D? FindParentGroup(Model3D geometryModel, Model3DGroup rootGroup)
    {
        if (rootGroup == null) return null;

        foreach (var child in rootGroup.Children)
        {
            if (ReferenceEquals(child, geometryModel))
            {
                return child;
            }

            if (child is Model3DGroup childGroup)
            {
                var found = FindParentGroup(geometryModel, childGroup);
                if (found != null) return childGroup;
            }
        }

        return null;
    }

    private void ClearSelection()
    {
        _selectedModel = null;
        _selectedLightSource = null;
        if (LightsListBox != null) LightsListBox.SelectedItem = null;
        UpdateVisualSelection();
        UpdateLightSettingsPanel();
    }

    private void UpdateVisualSelection()
    {
        try
        {
            if (_originalModel == null) return;

            ResetMaterialForModel(_originalModel);

            if (_selectedModel != null)
            {
                HighlightModel(_selectedModel);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обновления визуального выделения: {ex.Message}");
        }
    }

    private void ResetMaterialForModel(Model3D model)
    {
        if (model is GeometryModel3D gm)
        {
            ResetMaterial(gm);
        }
        else if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                ResetMaterialForModel(child);
            }
        }
    }

    private void HighlightModel(Model3D model)
    {
        if (model is GeometryModel3D gm)
        {
            var baseColor = Color.FromRgb(0xFF, 0xA5, 0x00);
            var diffuseBrush = new SolidColorBrush(baseColor);
            diffuseBrush.Freeze();

            var matGroup = new MaterialGroup();
            matGroup.Children.Add(new DiffuseMaterial(diffuseBrush));
            matGroup.Children.Add(new SpecularMaterial(
                new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), 30));

            gm.Material = matGroup;
            gm.BackMaterial = matGroup;
        }
        else if (model is Model3DGroup group)
        {
            foreach (var child in group.Children)
            {
                HighlightModel(child);
            }
        }
    }

    private void ResetMaterial(GeometryModel3D gm)
    {
        var baseColor = Color.FromRgb(0x4C, 0xAF, 0x50);
        var diffuseBrush = new SolidColorBrush(baseColor);
        diffuseBrush.Freeze();

        var matGroup = new MaterialGroup();
        matGroup.Children.Add(new DiffuseMaterial(diffuseBrush));
        matGroup.Children.Add(new SpecularMaterial(
            new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), 20));

        gm.Material = matGroup;
        gm.BackMaterial = matGroup;
    }

    public Vector3D GetCurrentPosition()
    {
        if (_selectedLightSource != null)
        {
            var pos = new Vector3D(_selectedLightSource.Position.X, _selectedLightSource.Position.Y, _selectedLightSource.Position.Z);
            Console.WriteLine($"[DEBUG] GetCurrentPosition: источник света, pos=({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2})");
            return pos;
        }

        if (_selectedModel == null)
        {
            Console.WriteLine("[DEBUG] GetCurrentPosition: _selectedModel == null, возврат (0,0,0)");
            return new Vector3D(0, 0, 0);
        }

        if (ReferenceEquals(_selectedModel, _originalModel) && _wholeModelTransform != null)
        {
            var translate = _wholeModelTransform.Children.OfType<TranslateTransform3D>().FirstOrDefault();
            if (translate != null)
            {
                var pos = new Vector3D(translate.OffsetX, translate.OffsetY, translate.OffsetZ);
                Console.WriteLine($"[DEBUG] GetCurrentPosition: вся модель, pos=({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2})");
                return pos;
            }
            Console.WriteLine("[DEBUG] GetCurrentPosition: вся модель, translate == null");
        }
        else if (_partTransforms.ContainsKey(_selectedModel))
        {
            var transform = _partTransforms[_selectedModel];
            var translate = transform.Children.OfType<TranslateTransform3D>().FirstOrDefault();
            if (translate != null)
            {
                var pos = new Vector3D(translate.OffsetX, translate.OffsetY, translate.OffsetZ);
                Console.WriteLine($"[DEBUG] GetCurrentPosition: часть модели, pos=({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2})");
                return pos;
            }
            Console.WriteLine("[DEBUG] GetCurrentPosition: часть модели, translate == null");
        }
        else
        {
            if (_wholeModelTransform != null)
            {
                var translate = _wholeModelTransform.Children.OfType<TranslateTransform3D>().FirstOrDefault();
                if (translate != null)
                {
                    var pos = new Vector3D(translate.OffsetX, translate.OffsetY, translate.OffsetZ);
                    Console.WriteLine($"[DEBUG] GetCurrentPosition: модель не найдена в _partTransforms, используем _wholeModelTransform, pos=({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2})");
                    return pos;
                }
            }
            Console.WriteLine($"[DEBUG] GetCurrentPosition: модель не найдена в _partTransforms, ReferenceEquals={ReferenceEquals(_selectedModel, _originalModel)}");
        }

        Console.WriteLine("[DEBUG] GetCurrentPosition: возврат (0,0,0) по умолчанию");
        return new Vector3D(0, 0, 0);
    }

    public void SetPosition(double x, double y, double z)
    {
        if (_selectedLightSource != null)
        {
            _selectedLightSource.Position = new Point3D(x, y, z);
            UpdateLightPosition(_selectedLightSource);
            Console.WriteLine($"[DEBUG] SetPosition: источник света, установлена позиция ({x:F2}, {y:F2}, {z:F2})");
            return;
        }

        if (_selectedModel == null)
        {
            Console.WriteLine("[DEBUG] SetPosition: _selectedModel == null, пропуск");
            return;
        }

        Console.WriteLine($"[DEBUG] SetPosition: ({x:F2}, {y:F2}, {z:F2}), selectedModel={_selectedModel.GetHashCode()}, originalModel={_originalModel?.GetHashCode()}");

        if (ReferenceEquals(_selectedModel, _originalModel) && _wholeModelTransform != null)
        {
            var translate = _wholeModelTransform.Children.OfType<TranslateTransform3D>().FirstOrDefault();
            if (translate != null)
            {
                translate.OffsetX = x;
                translate.OffsetY = y;
                translate.OffsetZ = z;
                Console.WriteLine($"[DEBUG] SetPosition: установлена позиция для всей модели через _wholeModelTransform");
            }
            else
            {
                Console.WriteLine("[DEBUG] SetPosition: translate == null в _wholeModelTransform");
            }
        }
        else if (_partTransforms.ContainsKey(_selectedModel))
        {
            var transform = _partTransforms[_selectedModel];
            var translate = transform.Children.OfType<TranslateTransform3D>().FirstOrDefault();
            if (translate != null)
            {
                translate.OffsetX = x;
                translate.OffsetY = y;
                translate.OffsetZ = z;
                Console.WriteLine($"[DEBUG] SetPosition: установлена позиция для части модели через _partTransforms");
            }
            else
            {
                Console.WriteLine("[DEBUG] SetPosition: translate == null в _partTransforms");
            }
        }
        else
        {
            if (_wholeModelTransform != null)
            {
                var translate = _wholeModelTransform.Children.OfType<TranslateTransform3D>().FirstOrDefault();
                if (translate != null)
                {
                    translate.OffsetX = x;
                    translate.OffsetY = y;
                    translate.OffsetZ = z;
                    Console.WriteLine($"[DEBUG] SetPosition: модель не найдена в _partTransforms, используем _wholeModelTransform");
                }
            }
            else
            {
                Console.WriteLine($"[DEBUG] SetPosition: модель не найдена! ReferenceEquals={ReferenceEquals(_selectedModel, _originalModel)}, ContainsKey={_partTransforms.ContainsKey(_selectedModel)}, _wholeModelTransform==null");
            }
        }
    }

    private void UpdateTransformControls()
    {
        if (TransformPanel == null) return;

        _updatingControls = true;

        try
        {
            var pos = GetCurrentPosition();

            var posXText = FindName("PositionXText") as TextBox;
            var posYText = FindName("PositionYText") as TextBox;
            var posZText = FindName("PositionZText") as TextBox;

            if (posXText != null) posXText.Text = pos.X.ToString("F2");
            if (posYText != null) posYText.Text = pos.Y.ToString("F2");
            if (posZText != null) posZText.Text = pos.Z.ToString("F2");

            var selectedLabel = FindTextBlock("SelectedItemLabel") ?? SelectedItemLabel;
            if (selectedLabel != null)
            {
                string name = "Ничего не выбрано";
                if (_selectedLightSource != null)
                {
                    name = _selectedLightSource.Name;
                    Console.WriteLine($"[DEBUG] UpdateTransformControls: выбран источник света {name}");
                }
                else if (_selectedModel != null)
                {
                    if (_rootTreeNode != null)
                    {
                        var node = FindTreeNode(_rootTreeNode, _selectedModel);
                        name = node?.Name ?? "Выбрано";
                        Console.WriteLine($"[DEBUG] UpdateTransformControls: найден узел {name} для модели {_selectedModel.GetHashCode()}");
                    }
                    else
                    {
                        name = "Выбрано";
                        Console.WriteLine($"[DEBUG] UpdateTransformControls: _rootTreeNode == null, используем 'Выбрано'");
                    }
                }
                else
                {
                    Console.WriteLine("[DEBUG] UpdateTransformControls: ничего не выбрано, показываем 'Ничего не выбрано'");
                }
                selectedLabel.Text = $"Выбрано: {name}";
                Console.WriteLine($"[DEBUG] UpdateTransformControls: установлен текст '{selectedLabel.Text}'");
            }
            else
            {
                Console.WriteLine("[DEBUG] UpdateTransformControls: selectedLabel == null");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обновления контролов: {ex.Message}");
        }
        finally
        {
            _updatingControls = false;
        }

        try
        {
            UpdateGizmos();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка обновления gizmos: {ex.Message}");
        }
    }

    private void PositionChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingControls) return;

        var posX = FindSlider("PositionX") ?? PositionX;
        var posY = FindSlider("PositionY") ?? PositionY;
        var posZ = FindSlider("PositionZ") ?? PositionZ;

        if (posX == null || posY == null || posZ == null) return;

        SetPosition(posX.Value, posY.Value, posZ.Value);
        UpdateGizmos();
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        SetPosition(0, 0, 0);
        UpdateTransformControls();
        StatusText.Text = "Позиция сброшена.";
    }

    private void AddLightButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "Добавить источник света",
            Width = 300,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this
        };

        var stack = new StackPanel { Margin = new Thickness(12) };

        var label = new TextBlock 
        { 
            Text = "Выберите тип источника:", 
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Colors.White)
        };
        stack.Children.Add(label);

        var directionalBtn = new Button { Content = "Directional (Направленный)", Margin = new Thickness(0, 0, 0, 4), Height = 30 };
        var pointBtn = new Button { Content = "Point (Точечный)", Margin = new Thickness(0, 0, 0, 4), Height = 30 };
        var spotBtn = new Button { Content = "Spot (Прожектор)", Margin = new Thickness(0, 0, 0, 4), Height = 30 };

        directionalBtn.Click += (s, args) => { AddLightSource(LightType.Directional); dialog.Close(); };
        pointBtn.Click += (s, args) => { AddLightSource(LightType.Point); dialog.Close(); };
        spotBtn.Click += (s, args) => { AddLightSource(LightType.Spot); dialog.Close(); };

        stack.Children.Add(directionalBtn);
        stack.Children.Add(pointBtn);
        stack.Children.Add(spotBtn);

        dialog.Content = stack;
        dialog.ShowDialog();
    }

    private void AddLightSource(LightType type)
    {
        var lightSource = new LightSource
        {
            Name = $"{type} Light {_lightSources.Count + 1}",
            Type = type,
            Color = Colors.White,
            Intensity = 1.0,
            Position = new Point3D(5, 5, 5),
            Direction = new Vector3D(0, -1, 0),
            RayCount = 10
        };

        _lightSources.Add(lightSource);

        LightsListBox.SelectedItem = lightSource;

        CreateLightInScene(lightSource);
        UpdateLightIndicators();
        UpdateLightSettingsPanel();

        StatusText.Text = $"Добавлен источник света: {lightSource.Name}";
    }

    private void CreateLightInScene(LightSource lightSource)
    {
        Light? light = null;

        switch (lightSource.Type)
        {
            case LightType.Directional:
                var dirLight = new DirectionalLight
                {
                    Color = lightSource.Color,
                    Direction = lightSource.Direction
                };
                light = dirLight;
                break;

            case LightType.Point:
                var pointLight = new PointLight
                {
                    Color = lightSource.Color,
                    Position = lightSource.Position,
                    ConstantAttenuation = 1.0,
                    LinearAttenuation = 0.01,
                    QuadraticAttenuation = 0.001
                };
                light = pointLight;
                break;

            case LightType.Spot:
                var spotLight = new SpotLight
                {
                    Color = lightSource.Color,
                    Position = lightSource.Position,
                    Direction = lightSource.Direction,
                    InnerConeAngle = lightSource.SpotAngle * 0.5,
                    OuterConeAngle = lightSource.SpotAngle,
                    ConstantAttenuation = 1.0,
                    LinearAttenuation = 0.01,
                    QuadraticAttenuation = 0.001
                };
                light = spotLight;
                break;
        }

        if (light != null)
        {
            lightSource.LightModel = light;

            var lightGroup = new Model3DGroup();
            lightGroup.Children.Add(light);

            var visual = new ModelVisual3D { Content = lightGroup };
            lightSource.VisualContainer = visual;

            if (_lightsContainer != null)
            {
                _lightsContainer.Children.Add(visual);
            }
        }
    }

    private void UpdateLightsInScene()
    {
        if (_lightsContainer == null) return;

        _lightsContainer.Children.Clear();

        foreach (var lightSource in _lightSources)
        {
            if (lightSource.VisualContainer != null)
            {
                _lightsContainer.Children.Add(lightSource.VisualContainer);
            }
            else
            {
                CreateLightInScene(lightSource);
            }
        }

        UpdateRayVisualization();
    }

    private void LightsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedLightSource = LightsListBox.SelectedItem as LightSource;

        if (_selectedLightSource != null)
        {
            _selectedModel = null;
        }

        UpdateLightSettingsPanel();
    }

    private void UpdateLightSettingsPanel()
    {
        if (TransformPanel == null) return;

        try
        {
            UnregisterName("SelectedItemLabel");
        }
        catch { }
        try
        {
            UnregisterName("PositionXText");
        }
        catch { }
        try
        {
            UnregisterName("PositionYText");
        }
        catch { }
        try
        {
            UnregisterName("PositionZText");
        }
        catch { }

        TransformPanel.Children.Clear();

        if (_selectedLightSource == null)
        {
            ShowModelSettingsPanel();
            return;
        }

        _updatingControls = true;
        try
        {
            var title = new TextBlock
            {
                Text = $"Настройки: {_selectedLightSource.Name}",
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Foreground = new SolidColorBrush(Colors.White),
                Margin = new Thickness(0, 0, 0, 8)
            };
            TransformPanel.Children.Add(title);

            var typeLabel = new TextBlock
            {
                Text = $"Тип: {_selectedLightSource.Type}",
                Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
                Margin = new Thickness(0, 0, 0, 12)
            };
            TransformPanel.Children.Add(typeLabel);

            AddEditableValueToPanel("Позиция X:", _selectedLightSource.Position.X, 
                value => { _selectedLightSource.Position = new Point3D(value, _selectedLightSource.Position.Y, _selectedLightSource.Position.Z); UpdateLightPosition(_selectedLightSource); UpdateGizmos(); });

            AddEditableValueToPanel("Позиция Y:", _selectedLightSource.Position.Y,
                value => { _selectedLightSource.Position = new Point3D(_selectedLightSource.Position.X, value, _selectedLightSource.Position.Z); UpdateLightPosition(_selectedLightSource); UpdateGizmos(); });

            AddEditableValueToPanel("Позиция Z:", _selectedLightSource.Position.Z,
                value => { _selectedLightSource.Position = new Point3D(_selectedLightSource.Position.X, _selectedLightSource.Position.Y, value); UpdateLightPosition(_selectedLightSource); UpdateGizmos(); });

            if (_selectedLightSource.Type == LightType.Directional)
            {
                var directionLabel = new TextBlock
                {
                    Text = "Направление:",
                    Foreground = new SolidColorBrush(Colors.White),
                    Margin = new Thickness(0, 8, 0, 4)
                };
                TransformPanel.Children.Add(directionLabel);

                var directionCombo = new ComboBox
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                    Foreground = new SolidColorBrush(Colors.White),
                    Padding = new Thickness(4),
                    Margin = new Thickness(0, 0, 0, 12)
                };

                var itemContainerStyle = new Style(typeof(ComboBoxItem));
                itemContainerStyle.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))));
                itemContainerStyle.Setters.Add(new Setter(ComboBoxItem.ForegroundProperty, new SolidColorBrush(Colors.White)));
                itemContainerStyle.Setters.Add(new Setter(ComboBoxItem.BorderBrushProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))));
                var hoverTrigger = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
                hoverTrigger.Setters.Add(new Setter(ComboBoxItem.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33))));
                itemContainerStyle.Triggers.Add(hoverTrigger);
                directionCombo.ItemContainerStyle = itemContainerStyle;
                
                directionCombo.DropDownOpened += (s, e) =>
                {
                    var popup = directionCombo.Template?.FindName("PART_Popup", directionCombo) as System.Windows.Controls.Primitives.Popup;
                    if (popup != null && popup.Child is Panel panel)
                    {
                        panel.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
                    }
                    else if (popup != null && popup.Child is Control control)
                    {
                        control.Background = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
                    }
                };


                var directions = new Dictionary<string, Vector3D>
                {
                    { "+X", new Vector3D(1, 0, 0) },
                    { "-X", new Vector3D(-1, 0, 0) },
                    { "+Y", new Vector3D(0, 1, 0) },
                    { "-Y", new Vector3D(0, -1, 0) },
                    { "+Z", new Vector3D(0, 0, 1) },
                    { "-Z", new Vector3D(0, 0, -1) }
                };

                foreach (var dir in directions)
                {
                    directionCombo.Items.Add(dir.Key);
                }

                var currentDir = _selectedLightSource.Direction;
                currentDir.Normalize();
                string? selectedKey = null;
                foreach (var dir in directions)
                {
                    var normalizedDir = dir.Value;
                    normalizedDir.Normalize();
                    if (Math.Abs(Vector3D.DotProduct(currentDir, normalizedDir) - 1.0) < 0.01)
                    {
                        selectedKey = dir.Key;
                        break;
                    }
                }
                if (selectedKey != null)
                {
                    directionCombo.SelectedItem = selectedKey;
                }

                directionCombo.SelectionChanged += (s, e) =>
                {
                    if (directionCombo.SelectedItem is string selected && directions.ContainsKey(selected))
                    {
                        _selectedLightSource.Direction = directions[selected];
                        UpdateLightDirection(_selectedLightSource);
                    }
                };

                TransformPanel.Children.Add(directionCombo);
            }

            AddSliderToPanel("Количество лучей:", 1, 1000, _selectedLightSource.RayCount,
                value => { _selectedLightSource.RayCount = (int)value; if (_showRayVisualization && _selectedLightSource.ShowRays) UpdateRayVisualization(); });

            AddSliderToPanel("Длина лучей:", 1, 1000, _selectedLightSource.RayLength,
                value => { _selectedLightSource.RayLength = value; if (_showRayVisualization && _selectedLightSource.ShowRays) UpdateRayVisualization(); });

            AddSliderToPanel("Интенсивность:", 0, 2, _selectedLightSource.Intensity,
                value => { _selectedLightSource.Intensity = value; UpdateLightProperties(_selectedLightSource); });

            var showRaysCheck = new CheckBox
            {
                Content = "Показать лучи",
                IsChecked = _selectedLightSource.ShowRays,
                Foreground = new SolidColorBrush(Colors.White),
                Margin = new Thickness(0, 12, 0, 12)
            };
            showRaysCheck.Checked += (s, e) => { _selectedLightSource.ShowRays = true; UpdateRayVisualization(); };
            showRaysCheck.Unchecked += (s, e) => { _selectedLightSource.ShowRays = false; UpdateRayVisualization(); };
            TransformPanel.Children.Add(showRaysCheck);

            var deleteBtn = new Button
            {
                Content = "Удалить источник",
                Margin = new Thickness(0, 8, 0, 0)
            };
            deleteBtn.Click += DeleteLightSource_Click;
            TransformPanel.Children.Add(deleteBtn);

            UpdateGizmos();
        }
        finally
        {
            _updatingControls = false;
        }
    }

    private void ShowModelSettingsPanel()
    {
        if (TransformPanel == null)
        {
            Console.WriteLine("[DEBUG] ShowModelSettingsPanel: TransformPanel == null");
            return;
        }

        Console.WriteLine($"[DEBUG] ShowModelSettingsPanel: начало, _selectedModel={_selectedModel?.GetHashCode()}, Children.Count={TransformPanel.Children.Count}");

        bool panelIsEmpty = TransformPanel.Children.Count == 0 || 
            !TransformPanel.Children.OfType<TextBox>().Any(t => t.Name == "PositionXText");

        Console.WriteLine($"[DEBUG] ShowModelSettingsPanel: panelIsEmpty={panelIsEmpty}");

        if (panelIsEmpty)
        {
            Console.WriteLine("[DEBUG] ShowModelSettingsPanel: пересоздание элементов панели");
            RecreateModelPanelElements();
        }

        Console.WriteLine("[DEBUG] ShowModelSettingsPanel: вызов UpdateTransformControls");
        UpdateTransformControls();
        Console.WriteLine("[DEBUG] ShowModelSettingsPanel: завершено");
    }

    private void RecreateModelPanelElements()
    {
        if (TransformPanel == null) return;

        try
        {
            UnregisterName("SelectedItemLabel");
        }
        catch { }
        try
        {
            UnregisterName("PositionXText");
        }
        catch { }
        try
        {
            UnregisterName("PositionYText");
        }
        catch { }
        try
        {
            UnregisterName("PositionZText");
        }
        catch { }

        var title = new TextBlock
        {
            Text = "Перемещение",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14,
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(0, 0, 0, 8)
        };
        TransformPanel.Children.Add(title);

        var selectedLabel = new TextBlock
        {
            Text = "Ничего не выбрано",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        };
        TransformPanel.Children.Add(selectedLabel);

        selectedLabel.Name = "SelectedItemLabel";
        RegisterName("SelectedItemLabel", selectedLabel);

        CreatePositionSlider("X", "PositionX", "PositionXText");
        CreatePositionSlider("Y", "PositionY", "PositionYText");
        CreatePositionSlider("Z", "PositionZ", "PositionZText");

        var resetBtn = new Button
        {
            Content = "Сбросить позицию",
            Margin = new Thickness(0, 8, 0, 0)
        };
        resetBtn.Click += ResetPosition_Click;
        TransformPanel.Children.Add(resetBtn);

        var controlsTitle = new TextBlock
        {
            Text = "Управление:",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(0, 16, 0, 4)
        };
        TransformPanel.Children.Add(controlsTitle);

        var controlsText = new TextBlock
        {
            Text = "• ЛКМ + перетаскивание - вращение камеры\n• СКМ + перетаскивание - перемещение камеры\n• Колесико мыши - масштаб (адаптивный)\n• WASD - движение камеры (адаптивное)\n• Q/E - вверх/вниз\n• ЛКМ по модели - выбор\n• ViewCube - ориентация камеры",
            Foreground = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4)
        };
        TransformPanel.Children.Add(controlsText);
    }

    private void CreatePositionSlider(string axis, string sliderName, string textBoxName)
    {
        if (TransformPanel == null) return;

        var label = new TextBlock
        {
            Text = $"Позиция {axis}:",
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(0, 4, 0, 2)
        };
        TransformPanel.Children.Add(label);

        var textBox = new TextBox
        {
            Name = textBoxName,
            IsReadOnly = false,
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = new SolidColorBrush(Colors.White),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 0, 12)
        };

        textBox.LostFocus += (s, e) => {
            UpdatePositionFromTextBox(axis, textBox);
        };

        textBox.KeyDown += (s, e) => {
            if (e.Key == Key.Enter)
            {
                UpdatePositionFromTextBox(axis, textBox);
                textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
        };

        TransformPanel.Children.Add(textBox);

        RegisterName(textBoxName, textBox);
    }

    private void UpdatePositionFromTextBox(string axis, TextBox textBox)
    {
        if (_updatingControls) return;
        if (_selectedModel == null) return;

        if (double.TryParse(textBox.Text, out double value))
        {
            var pos = GetCurrentPosition();
            switch (axis)
            {
                case "X":
                    SetPosition(value, pos.Y, pos.Z);
                    break;
                case "Y":
                    SetPosition(pos.X, value, pos.Z);
                    break;
                case "Z":
                    SetPosition(pos.X, pos.Y, value);
                    break;
            }
            UpdateGizmos();
            textBox.Text = value.ToString("F2");
        }
        else
        {
            var pos = GetCurrentPosition();
            switch (axis)
            {
                case "X":
                    textBox.Text = pos.X.ToString("F2");
                    break;
                case "Y":
                    textBox.Text = pos.Y.ToString("F2");
                    break;
                case "Z":
                    textBox.Text = pos.Z.ToString("F2");
                    break;
            }
        }
    }

    private Slider? FindSlider(string name)
    {
        return FindName(name) as Slider ?? TransformPanel?.Children.OfType<Slider>().FirstOrDefault(s => s.Name == name);
    }

    private TextBlock? FindTextBlock(string name)
    {
        return FindName(name) as TextBlock ?? TransformPanel?.Children.OfType<TextBlock>().FirstOrDefault(t => t.Name == name);
    }

    private void DeleteLightSource_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_selectedLightSource == null) return;

            var lightToRemove = _selectedLightSource;

            _selectedLightSource = null;

            _lightSources.Remove(lightToRemove);

            if (LightsListBox != null)
            {
                LightsListBox.SelectedItem = null;
            }

            UpdateLightIndicators();
            UpdateRayVisualization();

            UpdateLightSettingsPanel();

            StatusText.Text = "Источник света удален";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Ошибка при удалении источника света: {ex.Message}";
            MessageBox.Show(this, $"Ошибка при удалении источника света:\n{ex.Message}", 
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddEditableValueToPanel(string label, double value, Action<double> onValueChanged)
    {
        if (TransformPanel == null) return;

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(0, 4, 0, 2)
        };
        TransformPanel.Children.Add(labelText);

        var textBox = new TextBox
        {
            Text = value.ToString("F2"),
            IsReadOnly = false,
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = new SolidColorBrush(Colors.White),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 0, 12)
        };

        textBox.LostFocus += (s, e) => {
            if (double.TryParse(textBox.Text, out double parsedValue))
            {
                onValueChanged(parsedValue);
                textBox.Text = parsedValue.ToString("F2");
            }
            else
            {
                textBox.Text = value.ToString("F2");
            }
        };

        textBox.KeyDown += (s, e) => {
            if (e.Key == Key.Enter)
            {
                textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
        };

        TransformPanel.Children.Add(textBox);
    }

    private void AddSliderToPanel(string label, double min, double max, double value, Action<double> onValueChanged)
    {
        if (TransformPanel == null) return;

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = new SolidColorBrush(Colors.White),
            Margin = new Thickness(0, 4, 0, 2)
        };
        TransformPanel.Children.Add(labelText);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = (max - min) / 100,
            IsSnapToTickEnabled = false,
            Margin = new Thickness(0, 0, 0, 4)
        };

        var textBox = new TextBox
        {
            Text = value.ToString("F2"),
            IsReadOnly = false,
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground = new SolidColorBrush(Colors.White),
            Padding = new Thickness(4),
            Margin = new Thickness(0, 0, 0, 12)
        };

        slider.ValueChanged += (s, e) => {
            if (!_updatingControls)
            {
                textBox.Text = slider.Value.ToString("F2");
                onValueChanged(slider.Value);
            }
        };

        textBox.LostFocus += (s, e) => {
            if (double.TryParse(textBox.Text, out double parsedValue))
            {
                parsedValue = Math.Max(min, Math.Min(max, parsedValue));
                slider.Value = parsedValue;
                textBox.Text = parsedValue.ToString("F2");
                if (!_updatingControls)
                {
                    onValueChanged(parsedValue);
                }
            }
            else
            {
                textBox.Text = slider.Value.ToString("F2");
            }
        };

        textBox.KeyDown += (s, e) => {
            if (e.Key == Key.Enter)
            {
                textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            }
        };

        TransformPanel.Children.Add(slider);
        TransformPanel.Children.Add(textBox);
    }

    private void UpdateLightPosition(LightSource lightSource)
    {
        if (lightSource.LightModel is PointLight pointLight)
        {
            pointLight.Position = lightSource.Position;
        }
        else if (lightSource.LightModel is SpotLight spotLight)
        {
            spotLight.Position = lightSource.Position;
        }

        UpdateLightIndicators();

        if (_showRayVisualization)
        {
            UpdateRayVisualization();
        }
    }

    private void UpdateLightDirection(LightSource lightSource)
    {
        if (lightSource.LightModel is DirectionalLight dirLight)
        {
            dirLight.Direction = lightSource.Direction;
        }
        else if (lightSource.LightModel is SpotLight spotLight)
        {
            spotLight.Direction = lightSource.Direction;
        }

        UpdateLightIndicators();

        if (_showRayVisualization)
        {
            UpdateRayVisualization();
        }
    }

    private void UpdateLightProperties(LightSource lightSource)
    {
        if (lightSource.LightModel != null)
        {
            var adjustedColor = Color.Multiply(lightSource.Color, (float)lightSource.Intensity);
            adjustedColor = Color.FromArgb(adjustedColor.A, 
                (byte)Math.Min(255, (int)adjustedColor.R),
                (byte)Math.Min(255, (int)adjustedColor.G),
                (byte)Math.Min(255, (int)adjustedColor.B));

            lightSource.LightModel.Color = adjustedColor;
        }
    }

    private void ToggleRaysButton_Click(object sender, RoutedEventArgs e)
    {
        _showRayVisualization = !_showRayVisualization;

        if (ToggleRaysButton != null)
        {
            ToggleRaysButton.Content = _showRayVisualization ? "Лучи: Вкл" : "Лучи: Выкл";
        }

        if (_showRayVisualization)
        {
            foreach (var lightSource in _lightSources)
            {
                lightSource.ShowRays = true;
            }
        }

        UpdateRayVisualization();
        StatusText.Text = _showRayVisualization ? "Визуализация лучей включена" : "Визуализация лучей выключена";
    }

    private void UpdateRayVisualization()
    {
        Console.WriteLine($"[DEBUG] UpdateRayVisualization: начало, _raysContainer={_raysContainer?.GetHashCode()}, _showRayVisualization={_showRayVisualization}, источников={_lightSources.Count}");

        if (_raysContainer == null)
        {
            Console.WriteLine("[DEBUG] UpdateRayVisualization: _raysContainer == null, выход");
            return;
        }

        _raysContainer.Children.Clear();
        Console.WriteLine("[DEBUG] UpdateRayVisualization: контейнер лучей очищен");

        if (!_showRayVisualization)
        {
            Console.WriteLine("[DEBUG] UpdateRayVisualization: визуализация выключена, выход");
            return;
        }

        int raysGenerated = 0;
        foreach (var lightSource in _lightSources)
        {
            if (lightSource.ShowRays)
            {
                Console.WriteLine($"[DEBUG] UpdateRayVisualization: генерация лучей для {lightSource.Name}, позиция=({lightSource.Position.X:F2}, {lightSource.Position.Y:F2}, {lightSource.Position.Z:F2}), RayCount={lightSource.RayCount}");
                GenerateRaysForLight(lightSource);
                raysGenerated++;
            }
        }

        Console.WriteLine($"[DEBUG] UpdateRayVisualization: завершено, сгенерировано лучей для {raysGenerated} источников, всего визуалов в контейнере: {_raysContainer.Children.Count}");
    }

    private void GenerateRaysForLight(LightSource lightSource)
    {
        Console.WriteLine($"[DEBUG] GenerateRaysForLight: начало для {lightSource.Name}, тип={lightSource.Type}, позиция=({lightSource.Position.X:F2}, {lightSource.Position.Y:F2}, {lightSource.Position.Z:F2}), RayCount={lightSource.RayCount}, RayLength={lightSource.RayLength}");

        var rayLength = lightSource.RayLength;
        var rayGroup = new Model3DGroup();

        switch (lightSource.Type)
        {
            case LightType.Directional:
                var gridSize = (int)Math.Ceiling(Math.Sqrt(lightSource.RayCount));
                var spacing = 2.0;
                var startOffset = -(gridSize - 1) * spacing / 2.0;

                var dir = lightSource.Direction;
                dir.Normalize();
                var up = new Vector3D(0, 1, 0);
                if (Math.Abs(Vector3D.DotProduct(dir, up)) > 0.9)
                    up = new Vector3D(1, 0, 0);
                var right = Vector3D.CrossProduct(dir, up);
                right.Normalize();
                var forward = Vector3D.CrossProduct(right, dir);
                forward.Normalize();

                Console.WriteLine($"[DEBUG] GenerateRaysForLight: Directional, gridSize={gridSize}, spacing={spacing}, dir=({dir.X:F2}, {dir.Y:F2}, {dir.Z:F2})");

                for (int i = 0; i < lightSource.RayCount; i++)
                {
                    var x = startOffset + (i % gridSize) * spacing;
                    var z = startOffset + (i / gridSize) * spacing;
                    var start = lightSource.Position + right * x + forward * z;

                    GenerateRayWithReflections(rayGroup, start, dir, rayLength, lightSource.Color, 2);
                }
                Console.WriteLine($"[DEBUG] GenerateRaysForLight: Directional, сгенерировано {lightSource.RayCount} лучей, добавлено моделей в rayGroup: {rayGroup.Children.Count}");
                break;

            case LightType.Point:
                Console.WriteLine($"[DEBUG] GenerateRaysForLight: Point, позиция=({lightSource.Position.X:F2}, {lightSource.Position.Y:F2}, {lightSource.Position.Z:F2})");
                var angles = Math.Sqrt(lightSource.RayCount);
                var angleStep = Math.PI * 2.0 / angles;

                int rayIndex = 0;
                for (double phi = 0; phi < Math.PI * 2 && rayIndex < lightSource.RayCount; phi += angleStep)
                {
                    for (double theta = 0; theta < Math.PI && rayIndex < lightSource.RayCount; theta += angleStep)
                    {
                        var rayDir = new Vector3D(
                            Math.Sin(theta) * Math.Cos(phi),
                            Math.Cos(theta),
                            Math.Sin(theta) * Math.Sin(phi)
                        );
                        rayDir.Normalize();

                        var start = lightSource.Position;

                        GenerateRayWithReflections(rayGroup, start, rayDir, rayLength, lightSource.Color, 2);
                        rayIndex++;
                    }
                }
                Console.WriteLine($"[DEBUG] GenerateRaysForLight: Point, сгенерировано {rayIndex} лучей, добавлено моделей в rayGroup: {rayGroup.Children.Count}");
                break;

            case LightType.Spot:
                var coneAngle = Math.PI * lightSource.SpotAngle / 180.0;
                var raysPerRing = (int)Math.Ceiling(Math.Sqrt(lightSource.RayCount));

                rayIndex = 0;
                for (int ring = 0; ring < raysPerRing && rayIndex < lightSource.RayCount; ring++)
                {
                    var ringAngle = coneAngle * (ring + 1) / raysPerRing;
                    var raysInRing = raysPerRing - ring;

                    for (int r = 0; r < raysInRing && rayIndex < lightSource.RayCount; r++)
                    {
                        var angle = 2.0 * Math.PI * r / raysInRing;
                        var deviation = ringAngle * (new System.Random().NextDouble() * 0.8 + 0.1);

                        var perpendicular = Vector3D.CrossProduct(lightSource.Direction, new Vector3D(1, 0, 0));
                        if (perpendicular.Length < 0.1)
                            perpendicular = Vector3D.CrossProduct(lightSource.Direction, new Vector3D(0, 1, 0));
                        perpendicular.Normalize();

                        var rayDir = lightSource.Direction;
                        rayDir.Normalize();
                        rayDir = rayDir * Math.Cos(deviation) + perpendicular * Math.Sin(deviation) * Math.Cos(angle);
                        rayDir.Normalize();

                        var start = lightSource.Position;

                        GenerateRayWithReflections(rayGroup, start, rayDir, rayLength, lightSource.Color, 2);
                        rayIndex++;
                    }
                }
                break;
        }

        var visual = new ModelVisual3D { Content = rayGroup };
        _raysContainer!.Children.Add(visual);
        Console.WriteLine($"[DEBUG] GenerateRaysForLight: завершено для {lightSource.Name}, добавлен визуал в _raysContainer, всего визуалов: {_raysContainer.Children.Count}");
    }

    private GeometryModel3D CreateRayModel(Point3D start, Point3D end, Color color)
    {
        var geometry = new MeshGeometry3D();

        var direction = end - start;
        direction.Normalize();

        var up = new Vector3D(0, 1, 0);
        if (Math.Abs(Vector3D.DotProduct(direction, up)) > 0.9)
            up = new Vector3D(1, 0, 0);

        var right = Vector3D.CrossProduct(direction, up);
        right.Normalize();
        up = Vector3D.CrossProduct(right, direction);
        up.Normalize();

        var radius = 0.08;
        var segments = 16;
        var length = (end - start).Length;

        var positions = new Point3DCollection();
        var indices = new Int32Collection();
        var normals = new Vector3DCollection();

        for (int i = 0; i <= segments; i++)
        {
            var angle = 2.0 * Math.PI * i / segments;
            var offset = right * (radius * Math.Cos(angle)) + up * (radius * Math.Sin(angle));

            positions.Add(start + offset);
            positions.Add(end + offset);
            normals.Add(offset);
            normals.Add(offset);
        }

        for (int i = 0; i < segments; i++)
        {
            var baseIdx = i * 2;
            indices.Add(baseIdx);
            indices.Add(baseIdx + 1);
            indices.Add(baseIdx + 2);

            indices.Add(baseIdx + 2);
            indices.Add(baseIdx + 1);
            indices.Add(baseIdx + 3);
        }

        geometry.Positions = positions;
        geometry.TriangleIndices = indices;
        geometry.Normals = normals;

        var materialGroup = new MaterialGroup();
        var emissiveMaterial = new EmissiveMaterial(new SolidColorBrush(color));
        emissiveMaterial.Brush.Opacity = 1.0;
        materialGroup.Children.Add(emissiveMaterial);
        var diffuseMaterial = new DiffuseMaterial(new SolidColorBrush(color));
        diffuseMaterial.Brush.Opacity = 1.0;
        materialGroup.Children.Add(diffuseMaterial);

        return new GeometryModel3D { Geometry = geometry, Material = materialGroup };
    }

    private void GenerateRayWithReflections(Model3DGroup rayGroup, Point3D start, Vector3D direction, double maxLength, Color color, int maxReflections)
    {
        if (_originalModel == null) return;

        var currentStart = start;
        var currentDir = direction;
        currentDir.Normalize();
        var remainingLength = maxLength;
        var currentColor = color;

        for (int reflection = 0; reflection <= maxReflections && remainingLength > 0.1; reflection++)
        {
            var end = currentStart + currentDir * remainingLength;

            var hit = FindRayIntersection(currentStart, currentDir, remainingLength);

            if (hit.HasValue)
            {
                var hitPoint = hit.Value.Point;
                var hitNormal = hit.Value.Normal;

                rayGroup.Children.Add(CreateRayModel(currentStart, hitPoint, currentColor));

                var dotProduct = Vector3D.DotProduct(currentDir, hitNormal);
                var reflectedDir = currentDir - hitNormal * (2 * dotProduct);
                reflectedDir.Normalize();

                var traveledDistance = (hitPoint - currentStart).Length;
                remainingLength -= traveledDistance;

                currentStart = hitPoint + hitNormal * 0.01;
                currentDir = reflectedDir;

                currentColor = Color.Multiply(currentColor, 0.7f);
            }
            else
            {
                rayGroup.Children.Add(CreateRayModel(currentStart, end, currentColor));
                break;
            }
        }
    }

    private (Point3D Point, Vector3D Normal)? FindRayIntersection(Point3D rayStart, Vector3D rayDir, double maxDistance)
    {
        if (_modelWrapper == null) return null;

        var closestHit = (Point3D?)null;
        var closestNormal = (Vector3D?)null;
        var closestDistance = double.MaxValue;

        FindIntersectionsInModel(_modelWrapper, rayStart, rayDir, maxDistance, ref closestHit, ref closestNormal, ref closestDistance);

        if (closestHit.HasValue && closestNormal.HasValue)
        {
            return (closestHit.Value, closestNormal.Value);
        }

        return null;
    }

    private void FindIntersectionsInModel(Model3D model, Point3D rayStart, Vector3D rayDir, double maxDistance, 
        ref Point3D? closestHit, ref Vector3D? closestNormal, ref double closestDistance)
    {
        if (model is GeometryModel3D geomModel && geomModel.Geometry is MeshGeometry3D mesh)
        {
            if (mesh.Positions != null && mesh.TriangleIndices != null)
            {
                var transform = GetModelTransform(model);

                for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                {
                    if (i + 2 < mesh.TriangleIndices.Count)
                    {
                        var i0 = mesh.TriangleIndices[i];
                        var i1 = mesh.TriangleIndices[i + 1];
                        var i2 = mesh.TriangleIndices[i + 2];

                        if (i0 < mesh.Positions.Count && i1 < mesh.Positions.Count && i2 < mesh.Positions.Count)
                        {
                            var p0 = transform != null ? transform.Transform(mesh.Positions[i0]) : mesh.Positions[i0];
                            var p1 = transform != null ? transform.Transform(mesh.Positions[i1]) : mesh.Positions[i1];
                            var p2 = transform != null ? transform.Transform(mesh.Positions[i2]) : mesh.Positions[i2];

                            var hit = RayTriangleIntersection(rayStart, rayDir, p0, p1, p2, maxDistance);
                            if (hit.HasValue)
                            {
                                var distance = (hit.Value - rayStart).Length;
                                if (distance < closestDistance && distance > 0.0001)
                                {
                                    closestDistance = distance;
                                    closestHit = hit.Value;

                                    var v1 = p1 - p0;
                                    var v2 = p2 - p0;
                                    var normal = Vector3D.CrossProduct(v1, v2);
                                    normal.Normalize();

                                    if (Vector3D.DotProduct(normal, rayDir) > 0)
                                    {
                                        normal = -normal;
                                    }

                                    closestNormal = normal;
                                }
                            }
                        }
                    }
                }
            }
        }
        else if (model is Model3DGroup group)
        {
            var groupTransform = group.Transform;

            foreach (var child in group.Children)
            {
                if (groupTransform != null)
                {
                    var invTransform = groupTransform.Inverse;
                    if (invTransform != null)
                    {
                        var localRayStart = invTransform.Transform(rayStart);
                        var localRayDir = invTransform.Transform(rayDir + new Point3D(0, 0, 0)) - invTransform.Transform(new Point3D(0, 0, 0));
                        localRayDir.Normalize();

                        FindIntersectionsInModelWithTransform(child, localRayStart, localRayDir, maxDistance, groupTransform, ref closestHit, ref closestNormal, ref closestDistance);
                    }
                }
                else
                {
                    FindIntersectionsInModel(child, rayStart, rayDir, maxDistance, ref closestHit, ref closestNormal, ref closestDistance);
                }
            }
        }
    }

    private void FindIntersectionsInModelWithTransform(Model3D model, Point3D localRayStart, Vector3D localRayDir, double maxDistance, 
        Transform3D parentTransform, ref Point3D? closestHit, ref Vector3D? closestNormal, ref double closestDistance)
    {
        if (model is GeometryModel3D geomModel && geomModel.Geometry is MeshGeometry3D mesh)
        {
            if (mesh.Positions != null && mesh.TriangleIndices != null)
            {
                for (int i = 0; i < mesh.TriangleIndices.Count; i += 3)
                {
                    if (i + 2 < mesh.TriangleIndices.Count)
                    {
                        var i0 = mesh.TriangleIndices[i];
                        var i1 = mesh.TriangleIndices[i + 1];
                        var i2 = mesh.TriangleIndices[i + 2];

                        if (i0 < mesh.Positions.Count && i1 < mesh.Positions.Count && i2 < mesh.Positions.Count)
                        {
                            var p0 = parentTransform.Transform(mesh.Positions[i0]);
                            var p1 = parentTransform.Transform(mesh.Positions[i1]);
                            var p2 = parentTransform.Transform(mesh.Positions[i2]);

                            var worldRayStart = parentTransform.Transform(localRayStart);
                            var worldRayDir = parentTransform.Transform(localRayDir + new Point3D(0, 0, 0)) - parentTransform.Transform(new Point3D(0, 0, 0));
                            worldRayDir.Normalize();

                            var hit = RayTriangleIntersection(worldRayStart, worldRayDir, p0, p1, p2, maxDistance);
                            if (hit.HasValue)
                            {
                                var distance = (hit.Value - worldRayStart).Length;
                                if (distance < closestDistance && distance > 0.0001)
                                {
                                    closestDistance = distance;
                                    closestHit = hit.Value;

                                    var v1 = p1 - p0;
                                    var v2 = p2 - p0;
                                    var normal = Vector3D.CrossProduct(v1, v2);
                                    normal.Normalize();

                                    if (Vector3D.DotProduct(normal, worldRayDir) > 0)
                                    {
                                        normal = -normal;
                                    }

                                    closestNormal = normal;
                                }
                            }
                        }
                    }
                }
            }
        }
        else if (model is Model3DGroup group)
        {
            var combinedTransform = new Transform3DGroup();
            if (parentTransform != null) combinedTransform.Children.Add(parentTransform);
            if (group.Transform != null) combinedTransform.Children.Add(group.Transform);

            foreach (var child in group.Children)
            {
                FindIntersectionsInModelWithTransform(child, localRayStart, localRayDir, maxDistance, combinedTransform, ref closestHit, ref closestNormal, ref closestDistance);
            }
        }
    }

    private Transform3D? GetModelTransform(Model3D model)
    {
        if (model is GeometryModel3D)
        {
            foreach (var kvp in _partTransforms)
            {
                if (kvp.Key == model)
                {
                    return kvp.Value;
                }
            }
        }

        return _wholeModelTransform;
    }

    private Point3D? RayTriangleIntersection(Point3D rayStart, Vector3D rayDir, Point3D v0, Point3D v1, Point3D v2, double maxDistance)
    {
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var h = Vector3D.CrossProduct(rayDir, edge2);
        var a = Vector3D.DotProduct(edge1, h);

        if (Math.Abs(a) < 0.0001) return null;

        var f = 1.0 / a;
        var s = rayStart - v0;
        var u = f * Vector3D.DotProduct(s, h);

        if (u < 0.0 || u > 1.0) return null;

        var q = Vector3D.CrossProduct(s, edge1);
        var v = f * Vector3D.DotProduct(rayDir, q);

        if (v < 0.0 || u + v > 1.0) return null;

        var t = f * Vector3D.DotProduct(edge2, q);

        if (t > 0.0001 && t <= maxDistance)
        {
            return rayStart + rayDir * t;
        }

        return null;
    }

    private void UpdateLightIndicators()
    {
        if (_lightIndicatorsContainer == null) return;

        _lightIndicatorsContainer.Children.Clear();

        foreach (var lightSource in _lightSources)
        {
            var indicator = CreateLightIndicator(lightSource);
            _lightIndicatorsContainer.Children.Add(indicator);
        }
    }

    private ModelVisual3D CreateLightIndicator(LightSource lightSource)
    {
        var group = new Model3DGroup();
        var iconPos = lightSource.Position;

        if (lightSource.Type == LightType.Directional)
        {
            var dir = lightSource.Direction;
            dir.Normalize();

            var arrowStart = iconPos - dir * 2.0;
            var arrowEnd = iconPos;
            var arrowGeometry = CreateConeGeometry(arrowStart, arrowEnd, 0.8, 8);
            var arrowMaterial = new EmissiveMaterial(new SolidColorBrush(lightSource.Color));
            arrowMaterial.Brush.Opacity = 0.8;
            group.Children.Add(new GeometryModel3D(arrowGeometry, arrowMaterial));

            var starIcon = CreateStarIcon(iconPos, lightSource.Color, 1.2);
            group.Children.Add(starIcon);
        }
        else
        {
            var starIcon = CreateStarIcon(iconPos, lightSource.Color, 1.5);
            group.Children.Add(starIcon);
        }

        var visual = new ModelVisual3D { Content = group };
        visual.SetValue(System.Windows.FrameworkElement.TagProperty, lightSource);

        return visual;
    }

    private GeometryModel3D CreateStarIcon(Point3D center, Color color, double size)
    {
        var sphere = CreateSphereGeometry(center, size, 16, 16);

        var materialGroup = new MaterialGroup();
        var emissiveMaterial = new EmissiveMaterial(new SolidColorBrush(color));
        emissiveMaterial.Brush.Opacity = 1.0;
        materialGroup.Children.Add(emissiveMaterial);
        materialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));

        return new GeometryModel3D
        {
            Geometry = sphere,
            Material = materialGroup
        };
    }

    private MeshGeometry3D CreateSphereGeometry(Point3D center, double radius, int thetaDiv, int phiDiv)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();

        for (int i = 0; i <= thetaDiv; i++)
        {
            double theta = i * Math.PI / thetaDiv;
            double sinTheta = Math.Sin(theta);
            double cosTheta = Math.Cos(theta);

            for (int j = 0; j <= phiDiv; j++)
            {
                double phi = j * 2.0 * Math.PI / phiDiv;
                double sinPhi = Math.Sin(phi);
                double cosPhi = Math.Cos(phi);

                var position = new Point3D(
                    center.X + radius * sinTheta * cosPhi,
                    center.Y + radius * cosTheta,
                    center.Z + radius * sinTheta * sinPhi
                );

                positions.Add(position);

                var normal = new Vector3D(sinTheta * cosPhi, cosTheta, sinTheta * sinPhi);
                normals.Add(normal);
            }
        }

        for (int i = 0; i < thetaDiv; i++)
        {
            for (int j = 0; j < phiDiv; j++)
            {
                int i0 = i * (phiDiv + 1) + j;
                int i1 = i0 + 1;
                int i2 = (i + 1) * (phiDiv + 1) + j;
                int i3 = i2 + 1;

                indices.Add(i0); indices.Add(i2); indices.Add(i1);
                indices.Add(i1); indices.Add(i2); indices.Add(i3);
            }
        }

        mesh.Positions = positions;
        mesh.Normals = normals;
        mesh.TriangleIndices = indices;

        return mesh;
    }

    private void InitializeGizmos()
    {
    }

    private void UpdateGizmos()
    {
        try
        {
            Console.WriteLine("[DEBUG] UpdateGizmos: начало");

            if (_gizmoContainer == null)
            {
                Console.WriteLine("[DEBUG] UpdateGizmos: _gizmoContainer == null");
                return;
            }

            if ((_selectedModel == null || _originalModel == null) && _selectedLightSource == null)
            {
                Console.WriteLine($"[DEBUG] UpdateGizmos: ничего не выбрано, _selectedModel={_selectedModel?.GetHashCode()}, _selectedLightSource={_selectedLightSource?.GetHashCode()}");
                _gizmoContainer.Children.Clear();
                _gizmoVisualX = null;
                _gizmoVisualY = null;
                _gizmoVisualZ = null;
                return;
            }

            var position = GetCurrentPosition();
            Console.WriteLine($"[DEBUG] UpdateGizmos: position={position.X:F2}, {position.Y:F2}, {position.Z:F2}");

            Point3D gizmoPos;
            double gizmoScale;

            if (_selectedLightSource != null)
            {
                gizmoPos = new Point3D(position.X, position.Y, position.Z);
                _initialModelCenter = gizmoPos;
                gizmoScale = 5.0;

                if (_gizmoVisualX != null && _gizmoVisualY != null && _gizmoVisualZ != null)
                {
                    Console.WriteLine("[DEBUG] UpdateGizmos: gizmos уже существуют, обновляем только позицию (источник света)");
                    UpdateGizmoPosition(gizmoPos);
                    return;
                }
            }
            else
            {
                var bounds = GetSelectedModelBounds();

                if (_gizmoVisualX != null && _gizmoVisualY != null && _gizmoVisualZ != null)
                {
                    Console.WriteLine("[DEBUG] UpdateGizmos: gizmos уже существуют, обновляем только позицию");
                    if (bounds.HasValue)
                    {
                        var center = bounds.Value.Location + new Vector3D(bounds.Value.SizeX / 2, bounds.Value.SizeY / 2, bounds.Value.SizeZ / 2);
                        gizmoPos = new Point3D(center.X + position.X, center.Y + position.Y, center.Z + position.Z);
                        _initialModelCenter = center;
                    }
                    else
                    {
                        gizmoPos = new Point3D(position.X, position.Y, position.Z);
                    }
                    Console.WriteLine($"[DEBUG] UpdateGizmos: обновление позиции gizmos на ({gizmoPos.X:F2}, {gizmoPos.Y:F2}, {gizmoPos.Z:F2})");
                    UpdateGizmoPosition(gizmoPos);
                    return;
                }

                Console.WriteLine("[DEBUG] UpdateGizmos: gizmos не существуют, создаем новые");
                _gizmoContainer.Children.Clear();

                if (bounds.HasValue)
                {
                    var center = bounds.Value.Location + new Vector3D(bounds.Value.SizeX / 2, bounds.Value.SizeY / 2, bounds.Value.SizeZ / 2);
                    _initialModelCenter = center;
                    gizmoPos = new Point3D(center.X + position.X, center.Y + position.Y, center.Z + position.Z);
                    Console.WriteLine($"[DEBUG] UpdateGizmos: bounds OK, center (без трансформации)=({center.X:F2}, {center.Y:F2}, {center.Z:F2}), position (трансформация)=({position.X:F2}, {position.Y:F2}, {position.Z:F2}), gizmoPos=({gizmoPos.X:F2}, {gizmoPos.Y:F2}, {gizmoPos.Z:F2})");

                    var maxSize = Math.Max(Math.Max(bounds.Value.SizeX, bounds.Value.SizeY), bounds.Value.SizeZ);
                    gizmoScale = maxSize * 1.5;
                    if (gizmoScale < 1.0) gizmoScale = 1.0;
                    Console.WriteLine($"[DEBUG] UpdateGizmos: gizmoScale={gizmoScale:F2} (maxSize={maxSize:F2})");
                }
                else
                {
                    _initialModelCenter = new Point3D(0, 0, 0);
                    gizmoPos = new Point3D(position.X, position.Y, position.Z);
                    gizmoScale = 10.0;
                    Console.WriteLine($"[DEBUG] UpdateGizmos: bounds null, использована позиция трансформации, gizmoPos=({gizmoPos.X:F2}, {gizmoPos.Y:F2}, {gizmoPos.Z:F2}), gizmoScale={gizmoScale:F2} (стандартный)");
                }
            }

            if (_selectedLightSource == null)
            {
                _gizmoContainer.Children.Clear();
            }

            Console.WriteLine($"[DEBUG] UpdateGizmos: вызов CreateGizmos с позицией ({gizmoPos.X:F2}, {gizmoPos.Y:F2}, {gizmoPos.Z:F2}) и масштабом {gizmoScale:F2}");
            CreateGizmos(gizmoPos, gizmoScale);
            Console.WriteLine("[DEBUG] UpdateGizmos: завершено успешно");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DEBUG] ОШИБКА UpdateGizmos: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private Rect3D? GetSelectedModelBounds()
    {
        if (_selectedModel == null)
        {
            Console.WriteLine("[DEBUG] GetSelectedModelBounds: _selectedModel == null");
            return null;
        }

        if (ReferenceEquals(_selectedModel, _originalModel))
        {
            var bounds = CalculateBounds(_originalModel);
            Console.WriteLine($"[DEBUG] GetSelectedModelBounds: вся модель, bounds={bounds?.ToString() ?? "null"}");
            return bounds;
        }

        var partBounds = CalculateBounds(_selectedModel);
        Console.WriteLine($"[DEBUG] GetSelectedModelBounds: часть модели, bounds={partBounds?.ToString() ?? "null"}");
        return partBounds;
    }

    private void CreateGizmos(Point3D position, double scale)
    {
        Console.WriteLine($"[DEBUG] CreateGizmos: начало, position=({position.X:F2}, {position.Y:F2}, {position.Z:F2}), scale={scale:F2}");

        _gizmoContainer!.Children.Clear();
        _gizmoVisualX = null;
        _gizmoVisualY = null;
        _gizmoVisualZ = null;

        _gizmoStartPosition = new Point3D(0, 0, 0);
        Console.WriteLine($"[DEBUG] CreateGizmos: _gizmoStartPosition=(0,0,0), _initialModelCenter=({_initialModelCenter.X:F2}, {_initialModelCenter.Y:F2}, {_initialModelCenter.Z:F2})");

        _gizmoX = CreateArrowGizmo(new Point3D(0, 0, 0), new Vector3D(1, 0, 0), Colors.Red, 'X', scale);
        var gizmoXGroup = new Model3DGroup();
        gizmoXGroup.Children.Add(_gizmoX);
        _gizmoVisualX = new ModelVisual3D { Content = gizmoXGroup };
        _gizmoVisualX.SetValue(System.Windows.FrameworkElement.TagProperty, 'X');
        _gizmoContainer.Children.Add(_gizmoVisualX);

        _gizmoY = CreateArrowGizmo(new Point3D(0, 0, 0), new Vector3D(0, 1, 0), Colors.Green, 'Y', scale);
        var gizmoYGroup = new Model3DGroup();
        gizmoYGroup.Children.Add(_gizmoY);
        _gizmoVisualY = new ModelVisual3D { Content = gizmoYGroup };
        _gizmoVisualY.SetValue(System.Windows.FrameworkElement.TagProperty, 'Y');
        _gizmoContainer.Children.Add(_gizmoVisualY);

        _gizmoZ = CreateArrowGizmo(new Point3D(0, 0, 0), new Vector3D(0, 0, 1), Colors.Blue, 'Z', scale);
        var gizmoZGroup = new Model3DGroup();
        gizmoZGroup.Children.Add(_gizmoZ);
        _gizmoVisualZ = new ModelVisual3D { Content = gizmoZGroup };
        _gizmoVisualZ.SetValue(System.Windows.FrameworkElement.TagProperty, 'Z');
        _gizmoContainer.Children.Add(_gizmoVisualZ);

        var ringRadius = scale * 0.7;
        var ringThickness = scale * 0.02;
        
        _rotationGizmoX = CreateRotationRing(new Vector3D(1, 0, 0), Colors.Red, 'r', ringRadius, ringThickness);
        var rotGizmoXGroup = new Model3DGroup();
        rotGizmoXGroup.Children.Add(_rotationGizmoX);
        _rotationGizmoVisualX = new ModelVisual3D { Content = rotGizmoXGroup };
        _rotationGizmoVisualX.SetValue(System.Windows.FrameworkElement.TagProperty, 'r');
        _gizmoContainer.Children.Add(_rotationGizmoVisualX);

        _rotationGizmoY = CreateRotationRing(new Vector3D(0, 1, 0), Colors.Green, 'r', ringRadius, ringThickness);
        var rotGizmoYGroup = new Model3DGroup();
        rotGizmoYGroup.Children.Add(_rotationGizmoY);
        _rotationGizmoVisualY = new ModelVisual3D { Content = rotGizmoYGroup };
        _rotationGizmoVisualY.SetValue(System.Windows.FrameworkElement.TagProperty, 'r');
        _gizmoContainer.Children.Add(_rotationGizmoVisualY);

        _rotationGizmoZ = CreateRotationRing(new Vector3D(0, 0, 1), Colors.Blue, 'r', ringRadius, ringThickness);
        var rotGizmoZGroup = new Model3DGroup();
        rotGizmoZGroup.Children.Add(_rotationGizmoZ);
        _rotationGizmoVisualZ = new ModelVisual3D { Content = rotGizmoZGroup };
        _rotationGizmoVisualZ.SetValue(System.Windows.FrameworkElement.TagProperty, 'r');
        _gizmoContainer.Children.Add(_rotationGizmoVisualZ);

        Console.WriteLine("[DEBUG] CreateGizmos: gizmos созданы, вызов UpdateGizmoPosition");
        UpdateGizmoPosition(position);
        Console.WriteLine("[DEBUG] CreateGizmos: завершено");
    }

    private void UpdateGizmoPosition(Point3D newPosition)
    {
        if (_gizmoContainer == null || _gizmoVisualX == null || _gizmoVisualY == null || _gizmoVisualZ == null)
        {
            Console.WriteLine($"[DEBUG] UpdateGizmoPosition: пропуск - null references");
            return;
        }

        Console.WriteLine($"[DEBUG] UpdateGizmoPosition: newPosition=({newPosition.X:F2}, {newPosition.Y:F2}, {newPosition.Z:F2}), _gizmoStartPosition=({_gizmoStartPosition.X:F2}, {_gizmoStartPosition.Y:F2}, {_gizmoStartPosition.Z:F2})");

        var translate = new TranslateTransform3D(newPosition.X, newPosition.Y, newPosition.Z);

        _gizmoVisualX.Transform = translate;
        _gizmoVisualY.Transform = translate;
        _gizmoVisualZ.Transform = translate;
        
        if (_rotationGizmoVisualX != null) _rotationGizmoVisualX.Transform = translate;
        if (_rotationGizmoVisualY != null) _rotationGizmoVisualY.Transform = translate;
        if (_rotationGizmoVisualZ != null) _rotationGizmoVisualZ.Transform = translate;
    }

    private GeometryModel3D CreateArrowGizmo(Point3D start, Vector3D direction, Color color, char axis, double scale)
    {
        direction.Normalize();
        var length = scale;
        var arrowLength = length * 0.15;
        var arrowRadius = length * 0.05;
        var shaftRadius = length * 0.02;

        var end = start + direction * (length - arrowLength);
        var arrowEnd = start + direction * length;

        var shaftGeometry = CreateCylinderGeometry(start, end, shaftRadius, 16);

        var coneGeometry = CreateConeGeometry(end, arrowEnd, arrowRadius, 16);

        var combinedGeometry = CombineGeometries(new[] { shaftGeometry, coneGeometry });

        var materialGroup = new MaterialGroup();
        var emissiveMaterial = new EmissiveMaterial(new SolidColorBrush(color));
        emissiveMaterial.Brush.Opacity = 1.0;
        materialGroup.Children.Add(emissiveMaterial);
        materialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));

        var model = new GeometryModel3D(combinedGeometry, materialGroup);

        model.SetValue(System.Windows.FrameworkElement.TagProperty, axis);

        return model;
    }

    private MeshGeometry3D CreateCylinderGeometry(Point3D start, Point3D end, double radius, int segments)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();

        var direction = end - start;
        direction.Normalize();

        var up = new Vector3D(0, 1, 0);
        if (Math.Abs(Vector3D.DotProduct(direction, up)) > 0.9)
            up = new Vector3D(1, 0, 0);

        var right = Vector3D.CrossProduct(direction, up);
        right.Normalize();
        up = Vector3D.CrossProduct(right, direction);
        up.Normalize();

        for (int i = 0; i <= segments; i++)
        {
            double angle = 2.0 * Math.PI * i / segments;
            var offset = right * (radius * Math.Cos(angle)) + up * (radius * Math.Sin(angle));
            var normal = offset;
            normal.Normalize();

            positions.Add(start + offset);
            positions.Add(end + offset);
            normals.Add(normal);
            normals.Add(normal);
        }

        for (int i = 0; i < segments; i++)
        {
            int baseIdx = i * 2;
            indices.Add(baseIdx);
            indices.Add(baseIdx + 2);
            indices.Add(baseIdx + 1);

            indices.Add(baseIdx + 1);
            indices.Add(baseIdx + 2);
            indices.Add(baseIdx + 3);
        }

        mesh.Positions = positions;
        mesh.Normals = normals;
        mesh.TriangleIndices = indices;

        return mesh;
    }

    private MeshGeometry3D CreateConeGeometry(Point3D baseCenter, Point3D tip, double baseRadius, int segments)
    {
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();

        var direction = tip - baseCenter;
        direction.Normalize();

        var up = new Vector3D(0, 1, 0);
        if (Math.Abs(Vector3D.DotProduct(direction, up)) > 0.9)
            up = new Vector3D(1, 0, 0);

        var right = Vector3D.CrossProduct(direction, up);
        right.Normalize();
        up = Vector3D.CrossProduct(right, direction);
        up.Normalize();

        positions.Add(tip);
        normals.Add(direction);

        for (int i = 0; i <= segments; i++)
        {
            double angle = 2.0 * Math.PI * i / segments;
            var offset = right * (baseRadius * Math.Cos(angle)) + up * (baseRadius * Math.Sin(angle));
            positions.Add(baseCenter + offset);

            var toTip = tip - (baseCenter + offset);
            toTip.Normalize();
            var sideNormal = (toTip + direction) / 2;
            sideNormal.Normalize();
            normals.Add(sideNormal);
        }

        for (int i = 0; i < segments; i++)
        {
            indices.Add(0);
            indices.Add(i + 1);
            indices.Add(i + 2);
        }

        mesh.Positions = positions;
        mesh.Normals = normals;
        mesh.TriangleIndices = indices;

        return mesh;
    }

    private GeometryModel3D CreateRotationRing(Vector3D axis, Color color, char rotationAxis, double radius, double thickness)
    {
        axis.Normalize();
        var segments = 64;
        var mesh = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();

        var up = new Vector3D(0, 1, 0);
        if (Math.Abs(Vector3D.DotProduct(axis, up)) > 0.9)
            up = new Vector3D(1, 0, 0);

        var right = Vector3D.CrossProduct(axis, up);
        right.Normalize();
        var forward = Vector3D.CrossProduct(right, axis);
        forward.Normalize();

        var tubeRadius = thickness;
        var tubeSegments = 8;

            for (int i = 0; i <= segments; i++)
            {
                var angle = 2.0 * Math.PI * i / segments;
                var centerVec = right * (radius * Math.Cos(angle)) + forward * (radius * Math.Sin(angle));
                var center = new Point3D(centerVec.X, centerVec.Y, centerVec.Z);
                var tangent = -right * Math.Sin(angle) + forward * Math.Cos(angle);
                tangent.Normalize();

                for (int j = 0; j <= tubeSegments; j++)
                {
                    var tubeAngle = 2.0 * Math.PI * j / tubeSegments;
                    var tubeUp = Vector3D.CrossProduct(tangent, axis);
                    tubeUp.Normalize();
                    var tubeRight = Vector3D.CrossProduct(tubeUp, tangent);
                    tubeRight.Normalize();

                    var offset = tubeRight * (tubeRadius * Math.Cos(tubeAngle)) + tubeUp * (tubeRadius * Math.Sin(tubeAngle));
                    positions.Add(center + offset);

                var normal = offset;
                normal.Normalize();
                normals.Add(normal);
            }
        }

        for (int i = 0; i < segments; i++)
        {
            for (int j = 0; j < tubeSegments; j++)
            {
                var baseIdx = i * (tubeSegments + 1) + j;
                var nextBaseIdx = (i + 1) * (tubeSegments + 1) + j;

                indices.Add(baseIdx);
                indices.Add(baseIdx + 1);
                indices.Add(nextBaseIdx);

                indices.Add(baseIdx + 1);
                indices.Add(nextBaseIdx + 1);
                indices.Add(nextBaseIdx);
            }
        }

        mesh.Positions = positions;
        mesh.Normals = normals;
        mesh.TriangleIndices = indices;

        var materialGroup = new MaterialGroup();
        var emissiveMaterial = new EmissiveMaterial(new SolidColorBrush(color));
        emissiveMaterial.Brush.Opacity = 0.5;
        materialGroup.Children.Add(emissiveMaterial);
        materialGroup.Children.Add(new DiffuseMaterial(new SolidColorBrush(color)));

        var model = new GeometryModel3D(mesh, materialGroup);
        model.SetValue(System.Windows.FrameworkElement.TagProperty, rotationAxis);
        return model;
    }

    private MeshGeometry3D CombineGeometries(MeshGeometry3D[] geometries)
    {
        var combined = new MeshGeometry3D();
        var positions = new Point3DCollection();
        var normals = new Vector3DCollection();
        var indices = new Int32Collection();

        int offset = 0;
        foreach (var geom in geometries)
        {
            if (geom.Positions != null && geom.Normals != null && geom.TriangleIndices != null)
            {
                foreach (var pos in geom.Positions)
                    positions.Add(pos);

                foreach (var normal in geom.Normals)
                    normals.Add(normal);

                foreach (var index in geom.TriangleIndices)
                    indices.Add(index + offset);

                offset += geom.Positions.Count;
            }
        }

        combined.Positions = positions;
        combined.Normals = normals;
        combined.TriangleIndices = indices;

        return combined;
    }

    private void RaytraceButton_Click(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("[DEBUG] RaytraceButton_Click: начало");

        _showRayVisualization = true;

        if (ToggleRaysButton != null)
        {
            ToggleRaysButton.Content = "Лучи: Вкл";
        }

        Console.WriteLine($"[DEBUG] RaytraceButton_Click: включение ShowRays для {_lightSources.Count} источников");
        foreach (var lightSource in _lightSources)
        {
            lightSource.ShowRays = true;
            Console.WriteLine($"[DEBUG] RaytraceButton_Click: источник {lightSource.Name}, позиция=({lightSource.Position.X:F2}, {lightSource.Position.Y:F2}, {lightSource.Position.Z:F2})");
        }

        Console.WriteLine("[DEBUG] RaytraceButton_Click: вызов UpdateRayVisualization");
        UpdateRayVisualization();
        Console.WriteLine("[DEBUG] RaytraceButton_Click: завершено");

        StatusText.Text = "Рейтрейсинг запущен - визуализация лучей включена";
    }
}
