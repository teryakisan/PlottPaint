# PlottPaint 🎨

PlottPaint transforms your gantry-style plotter into an automated painting machine with intelligent brush stroke profiles, color management, and advanced path optimization.

[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)](https://www.microsoft.com/windows)

## ✨ Features

### 🎨 Painting Mode
- **Multi-Color Painting**: Define multiple paint wells with custom colors and positions
- **Intelligent Paint Management**: Automatic wash/wipe sequences on color changes
- **Paint Refresh Control**: Configurable paint refresh intervals to maintain consistent brush loading
- **Paint Order Tracking**: Respects the sequence in which colors were assigned to strokes

### 🖌️ Brush Stroke Profiles
- **Dynamic Z-Axis Control**: Apply pressure curves to strokes for realistic brush effects
- **Custom Profiles**: Create and manage reusable brush stroke profiles with pressure curves
- **Visual Profile Editor**: Interactive Bézier curve editor for designing brush behaviors
- **Profile Parameters**: 
  - Customizable sample count for smooth curves
  - Adjustable stroke speed per profile
  - Min/Max Z depth control
- **Paint Only Mode Integration**: Assign brush profiles to painting strokes after G-code generation

### 🎯 Drawing Tools
- **Vector Drawing**: Lines, rectangles, circles, polylines, and Bézier curves
- **Object Grouping**: Group/ungroup strokes, hierarchical parent groups
- **Subdivision**: Add intermediate points to strokes for finer control
- **Selection & Transform**: Select, move, copy, paste, and delete strokes
- **Snap Features**: Snap to endpoints, grid snapping with customizable spacing
- **Reference Images**: Load reference images for tracing and alignment

### 🖥️ Advanced Canvas Features
- **Paint Canvas Definition**: Define working area within the plotter bed
- **Popular Canvas Presets**: Quick selection of A-series, US Letter/Legal, and common art canvas sizes
- **Individual Margins**: Set different safe margins for each side
- **Margin Lock to Canvas**: Calculate margins from paint canvas edges
- **Auto-Rotation**: Automatically rotate canvas 90° to optimize bed usage
- **Zoom & Pan**: Smooth canvas navigation with mouse wheel zoom

### 📐 G-code Generation
- **Smart Path Optimization**: Minimize travel distance with nearest-neighbor optimization
- **Paint Mode Optimization**: Direction-only optimization that preserves paint order
- **Coordinate Transformation**: Automatic bed-to-work coordinate conversion
- **Custom G-code**: Configurable start/end G-code sequences
- **GRBL Integration**: Direct serial communication with GRBL controllers

### 🔍 Debugging & Visualization
- **G-code Path Visualizer**: Visual debugging of generated tool paths
- **Segment Analysis**: Find backtracks, long rapids, and disconnected segments
- **Paint Mode Visualization**: Color-coded painting strokes with sequence numbers
- **Brush Profile Indicators**: Visual markers for segments using brush profiles
- **Mark Bad Segments**: Flag problematic G-code segments for analysis

### 🎨 Paint Only View Mode
- **Post-Generation Editing**: Assign brush profiles to painting strokes after G-code generation
- **Stroke Selection**: Select individual or multiple painting strokes
- **Visual Feedback**: Color-coded strokes with stroke numbers and paint well indicators
- **Context Menu Actions**: Right-click to assign profiles or select all strokes
- **Live G-code Regeneration**: Automatically regenerates G-code when profiles are assigned

### 🌓 Modern UI
- **Dark/Light Theme**: Toggle between dark and light modes
- **Themed Components**: Consistent theming across all windows and controls
- **Drag Value Controls**: Drag numeric fields up/down to adjust values
- **Responsive Layout**: Adaptive UI that works at different window sizes
- **Tool Palette**: Floating tool selection window

## 🚀 Getting Started

### Prerequisites

- Windows 10/11
- .NET 8.0 Runtime
- GRBL-based CNC plotter (for physical plotting)

### Installation

1. Clone the repository:
```bash
git clone https://github.com/teryakisan/PlottPaint.git
cd PlottPaint
```

2. Open the solution:
```bash
start NVSPlotter.sln
```

3. Build and run:
   - Press F5 in Visual Studio, or
   - `dotnet build` followed by `dotnet run`

## 📖 Usage Guide

### Basic Workflow

1. **Configure Machine Settings** (⚙ Machine Settings)
   - Set plotter bed size
   - Configure home position
   - Define safe margins

2. **Draw Your Design**
   - Use drawing tools (Line, Rectangle, Circle, Bezier, etc.)
   - Group related strokes for easier management
   - Load reference images if needed

3. **Set Up Paint Wells** (🎨 Painting Mode)
   - Click "🎨 Quick Setup" for default wells (Red, Green, Blue, Wash, Wipe)
   - Or add custom paint wells manually
   - Position wells on the canvas
   - Configure dip depth, dwell time, and refresh intervals

4. **Assign Colors**
   - Select strokes
   - Click on a paint well chip to assign that color
   - Use "None" to clear paint assignment

5. **Create Brush Profiles** (🖌 Profiles button)
   - Open Brush Stroke Profiles window
   - Create new profiles with custom pressure curves
   - Save profiles to your project

6. **Assign Brush Profiles** (Paint Only Mode)
   - Toggle "🎨 Paint Only" view
   - Select painting strokes
   - Right-click → "🖌 Assign Brush Profiles"
   - Choose which profiles to enable for selected strokes

7. **Generate G-code**
   - Click "Export .gcode" or "Copy G-code"
   - Review in the G-code visualizer (🔍 Path Debug)
   - Look for brush profile markers in the output

8. **Send to Plotter**
   - Connect to GRBL plotter via serial port
   - Click "Send G-code" to execute the plot

### Key Concepts

#### Paint Order
Strokes are painted in the order colors were assigned, not the drawing order. This allows you to:
- Draw everything first, then decide painting sequence
- Change colors on existing strokes without redrawing
- Have multiple objects use the same color at different times

#### Brush Stroke Profiles
Brush profiles control the Z-axis depth dynamically along a stroke, creating realistic brush effects:
- **t = 0.0**: Start of stroke
- **t = 1.0**: End of stroke
- The profile curve (0-1 on Y axis) maps to Z depth from ZUp to ZDown
- Profiles can simulate dabs, tapers, pressure variations, and more

#### Paint Only Mode
A special view mode that shows the actual painting sequence from generated G-code:
- View strokes in painting order with sequence numbers
- Assign brush profiles to individual painting strokes
- See stroke boundaries clearly (where pen goes up/down)
- Changes automatically regenerate G-code

## 🎨 Paint Wells

### Well Types
- **Paint Wells**: Colored paint sources
- **Wash Well**: Rinse/clean well (generates spiral swirl pattern)
- **Wipe Well**: Drying surface (generates zig-zag wipe pattern)

### Well Configuration
Each paint well has:
- **Position & Size**: Bounds on the plotter bed
- **Dip Depth**: How deep to dip into the paint
- **Dwell Time**: How long to wait while dipped
- **Refresh Distance**: Random range (min-max) for paint reload

## 🖌️ Brush Profiles

### Profile Editor
- **Interactive Curve**: Click to add control points, drag to adjust
- **Bézier Interpolation**: Smooth curves between control points
- **Sample Count**: Control smoothness (20-200 samples)
- **Stroke Speed**: Override default feed rate
- **Z Range**: Min/Max Z depth limits

### Built-in Profile Types
The profile editor supports creating various stroke styles:
- **Gentle Dab**: Light touch with gradual pressure
- **Hard Press**: Strong, consistent pressure
- **Taper In/Out**: Gradual pressure changes at start/end
- **Staccato**: Quick up-down motions
- **Variable Pressure**: Complex pressure curves

### Profile Application
Profiles are applied at the **G-code level**:
- The profile's Y curve (0-1) maps to Z depth
- `Y=0` → `Z=ZUp` (pen up/travel height)
- `Y=1` → `Z=ZDown` (maximum depth)
- Samples are distributed evenly along the stroke's physical length

## 🔧 Advanced Features

### Stroke Optimization
- **Normal Mode**: Full nearest-neighbor optimization (reorders all strokes)
- **Paint Mode**: Direction-only optimization (preserves color sequence)
- Toggle via "Optimize stroke order" checkbox

### Coordinate System
- **Document Space**: User-facing coordinates (origin at top-left, Y positive down)
- **Bed Space**: Transformed to plotter bed with scaling/rotation/margins
- **Work Space**: GRBL work coordinates (X positive, Y negative, origin at home)

### Project Files
Save and load complete projects including:
- All strokes and grouping information
- Paint wells and their configurations
- Brush stroke profiles
- Machine settings
- Canvas size and margins

## 🎯 Keyboard Shortcuts

### General
- `Ctrl+N` - New Project
- `Ctrl+O` - Open Project
- `Ctrl+S` - Save Project
- `Ctrl+Shift+S` - Save Project As
- `Ctrl+Z` - Undo

### Editing
- `Ctrl+A` - Select All
- `Ctrl+C` - Copy
- `Ctrl+X` - Cut
- `Ctrl+V` - Paste
- `Del` - Delete
- `Esc` - Deselect / Cancel operation

### Grouping
- `Ctrl+G` - Group selected strokes
- `Ctrl+Shift+G` - Ungroup

### Canvas
- `Mouse Wheel` - Zoom in/out
- `Middle Mouse + Drag` - Pan
- `Right Mouse` - Context menu

### G-code Visualizer
- `B` - Mark/unmark segment as bad
- `F` - Fit to view
- `Esc` - Deselect segment

## 🏗️ Architecture

### Core Services
- **GcodeGeneratorService**: Generates G-code from document strokes
- **GcodePaintingStrokeService**: Parses G-code to extract painting strokes
- **CoordinateTransformService**: Handles coordinate system transformations
- **CanvasRendererService**: Renders strokes on the WPF canvas
- **PaintWellController**: Manages paint well state and color assignments
- **PaintOnlyModeController**: Handles Paint Only view interactions
- **SelectionController**: Manages stroke selection and clipboard operations
- **GrblManagerService**: Serial communication with GRBL controllers

### Data Models
- **PlotDocument**: Main document containing strokes and paint wells
- **LineStroke**: Individual line segment with paint/group metadata
- **PaintWell**: Paint source definition with position and parameters
- **BrushProfile**: Brush stroke profile with Bézier curve
- **GcodePaintingStroke**: Parsed painting stroke from G-code
- **ProjectFile**: Complete project serialization

### Windows
- **MainWindow**: Primary application window with canvas and controls
- **BrushStrokeProfilesWindow**: Profile editor with curve designer
- **GcodePathVisualizerWindow**: G-code debugging and analysis
- **MachineSettingsWindow**: Plotter configuration
- **ReferenceImageWindow**: Reference image management
- **ToolsWindow**: Floating tool palette
- **ConsoleWindow**: GRBL console for manual commands

## 🤝 Contributing

Contributions are welcome! Please feel free to submit issues and pull requests.

### Development Setup
1. Install Visual Studio 2022 or later with .NET 8.0 SDK
2. Clone the repository
3. Open `NVSPlotter.sln`
4. Build and run

### Code Style
- Follow C# naming conventions
- Use nullable reference types
- Add XML documentation for public APIs
- Keep methods focused and single-purpose

## 🙏 Acknowledgments

- Built with WPF and .NET 8
- GRBL firmware compatibility
- Uses WpfAnimatedGif for loading indicators
- Emoji.Wpf for emoji rendering support
- FontAwesome for Icons

## 📧 Contact

- GitHub: [@teryakisan](https://github.com/teryakisan)
- Project: [PlottPaint](https://github.com/teryakisan/PlottPaint)

## 🗺️ Roadmap

### Planned Features
- [ ] SVG import/export
- [ ] Job queue management
- [ ] Machine calibration wizard

## 📸 Screenshots

<img width="99%" alt="Full_Interface_DK" src="https://github.com/user-attachments/assets/8b592083-8351-47dd-9bf0-8ed5cebef63d" />

<p float="left">
  <img width="49%"  alt="Machine_Settings" src="https://github.com/user-attachments/assets/8bcb7824-e24a-41cb-b594-bc8ef92e30fa" />
  <img width="49%"  alt="painting_mode" src="https://github.com/user-attachments/assets/7d5e4535-0d2d-4211-8a38-b1c12fb5636c" />
</p>

<img width="99%" alt="path_visualiser" src="https://github.com/user-attachments/assets/a8ec80b3-eff7-48a3-b3bc-8a7cccc8f879" />

<p float="left">
<img width="49%"  alt="brush_profiles" src="https://github.com/user-attachments/assets/a58da0ef-4c36-4229-9c5f-f2d0b9699175" />
<img width="49%" alt="Console" src="https://github.com/user-attachments/assets/8ba91f1d-692c-430e-8026-edc47930fcd9" />
<img width="49%" alt="gcode_settings" src="https://github.com/user-attachments/assets/b5fdc8b5-3f61-4249-87c8-785cfb94f8ae" />
<img width="49%" alt="plotter_settings" src="https://github.com/user-attachments/assets/fac270c6-d1b7-49d4-9b8b-a971e57eed72" />
<img width="49%" alt="subdivide" src="https://github.com/user-attachments/assets/d261bce2-e4e6-4d5d-86e4-b932f1dc117a" />  
</p>



---

**Made with ❤️ for the plotter art community by NVSInk Art Studio**
