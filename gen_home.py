path = "src/AppLock/WindowIsland/Host/WindowIslandWindow.xaml"
text = open(path, encoding="utf-8").read()

start = '                        <Grid x:Name="ContentHome" Opacity="1">'
end = '                    <!-- Clipboard tab -->'
i = text.index(start)
j = text.index(end)

I = " " * 24   # grid children of ContentHost
A = " " * 28   # inside ContentHome
B = " " * 32
C = " " * 36
D = " " * 40


def tile(row, col, name, click, glyph, label, tip, values=None):
    out = [
        '<Button x:Name="{0}"'.format(name),
        '        Grid.Row="{0}" Grid.Column="{1}"'.format(row, col),
        '        Click="{0}"'.format(click),
        '        Style="{StaticResource TileButtonStyle}"',
        '        ToolTipService.ToolTip="{0}">'.format(tip),
    ]
    if values:
        out += [
            '    <StackPanel Spacing="4" VerticalAlignment="Center">',
            '        <StackPanel Orientation="Horizontal" Spacing="7">',
            '            <FontIcon Glyph="{0}" FontSize="14" />'.format(glyph),
            '            <TextBlock Text="{0}" FontSize="11" />'.format(label),
            '        </StackPanel>',
            '        <StackPanel Orientation="Horizontal" Spacing="9">',
            '            ' + values[0],
            '            ' + values[1],
            '        </StackPanel>',
            '    </StackPanel>',
        ]
    else:
        out += [
            '    <StackPanel Orientation="Horizontal" Spacing="7" VerticalAlignment="Center">',
            '        <FontIcon Glyph="{0}" FontSize="14" />'.format(glyph),
            '        <TextBlock Text="{0}" FontSize="11" TextTrimming="CharacterEllipsis" />'.format(label),
            '    </StackPanel>',
        ]
    out.append('</Button>')
    return "\n".join([C + out[0]] + [C + line for line in out[1:]]) + "\n"


tiles = ""
tiles += tile(0, 0, "TileArea", "TileArea_Click", "&#xE7C8;", "Area",
              "Select an area to capture (Snipping Tool)")
tiles += tile(0, 1, "TileFullScreen", "TileFullScreen_Click", "&#xE8B9;", "Full screen",
              "Capture this monitor and copy it")
tiles += tile(1, 0, "TileScreenText", "TileScreenText_Click", "&#xE8D2;", "Screen text",
              "Copy text from the screen (Snipping Tool)")
tiles += tile(1, 1, "TileQR", "TileQR_Click", "&#xE774;", "QR code",
              "Scan a QR code on this monitor")
tiles += tile(2, 0, "TileNetwork", "TileNetwork_Click", "&#xE8BE;", "Network",
              "Download and upload speed",
              ('<TextBlock x:Name="TileNetDown" Text="&#x2193; &#8212;" FontSize="10" Foreground="#8FFFFFFF" />',
               '<TextBlock x:Name="TileNetUp" Text="&#x2191; &#8212;" FontSize="10" Foreground="#8FFFFFFF" />'))
tiles += tile(2, 1, "TileCpuRam", "TileCpuRam_Click", "&#xE7F4;", "CPU &amp; RAM",
              "CPU and memory load (opens Task Manager)",
              ('<TextBlock x:Name="TileCpuText" Text="CPU &#8212;" FontSize="10" Foreground="#8FFFFFFF" />',
               '<TextBlock x:Name="TileRamText" Text="RAM &#8212;" FontSize="10" Foreground="#8FFFFFFF" />'))
tiles += tile(3, 0, "TileRecord", "TileRecord_Click", "&#xE7A0;", "Record screen",
              "Record the screen (Snipping Tool)")
tiles += tile(3, 1, "TileCommand", "TileCommand_Click", "&#xE773;", "Command",
              "Open the command bar")


def recent(n, row):
    return """
{I}<Grid x:Name="RecentItem{n}" Grid.Row="{row}"
{I}      ColumnSpacing="10" Visibility="Collapsed">
{I}    <Grid.ColumnDefinitions>
{I}        <ColumnDefinition Width="Auto" />
{I}        <ColumnDefinition Width="*"    />
{I}        <ColumnDefinition Width="Auto" />
{I}    </Grid.ColumnDefinitions>
{I}    <Border Grid.Column="0" Width="26" Height="26" CornerRadius="7" Background="#16FFFFFF">
{I}        <FontIcon x:Name="RecentIcon{n}" Glyph="&#xE8C3;" FontSize="11" Foreground="#B3FFFFFF" />
{I}    </Border>
{I}    <TextBlock x:Name="RecentText{n}"
{I}               Grid.Column="1"
{I}               FontSize="11"
{I}               Foreground="#E6FFFFFF"
{I}               MaxLines="1"
{I}               TextTrimming="CharacterEllipsis"
{I}               VerticalAlignment="Center" />
{I}    <TextBlock x:Name="RecentAge{n}"
{I}               Grid.Column="2"
{I}               FontSize="9.5"
{I}               Foreground="#70FFFFFF"
{I}               VerticalAlignment="Center" />
{I}</Grid>
""".replace("{n}", str(n)).replace("{row}", str(row)).replace("{I}", I)


block = I + """<!-- Home tab - the panel is a set of cards on one surface grid:
{I}       top left   media player, with the lyrics card beside it when the track has lyrics
{I}       top right  the 2x4 tile grid
{I}       below      the volume bar, then the launcher across the full width
{I}     Every card shares one radius, one fill and one border so the spacing reads evenly. -->
{I}<Grid x:Name="ContentHome" Opacity="1">
{I}    <Grid.RowDefinitions>
{I}        <RowDefinition Height="*"  />
{I}        <RowDefinition Height="46" />
{I}    </Grid.RowDefinitions>

{I}    <!-- Upper block: player / lyrics / volume on the left, tiles on the right -->
{I}    <Grid Grid.Row="0">
{I}        <Grid.ColumnDefinitions>
{I}            <ColumnDefinition Width="*"   />
{I}            <ColumnDefinition Width="8"   />
{I}            <ColumnDefinition Width="240" />
{I}        </Grid.ColumnDefinitions>

{I}        <Grid Grid.Column="0">
{I}            <Grid.RowDefinitions>
{I}                <RowDefinition Height="*"  />
{I}                <RowDefinition Height="8"  />
{I}                <RowDefinition Height="38" />
{I}            </Grid.RowDefinitions>

{I}            <!-- Player card, with the lyrics card beside it -->
{I}            <Grid Grid.Row="0" ColumnSpacing="8">
{I}                <Grid.ColumnDefinitions>
{I}                    <ColumnDefinition Width="1.1*" />
{I}                    <ColumnDefinition Width="*"    />
{I}                </Grid.ColumnDefinitions>

{I}                <Border x:Name="MediaCard"
{I}                        Grid.Column="0"
{I}                        Grid.ColumnSpan="2"
{I}                        CornerRadius="14"
{I}                        Background="#10FFFFFF"
{I}                        BorderBrush="#1AFFFFFF"
{I}                        BorderThickness="1"
{I}                        Padding="12,11,12,11">
{I}                    <Grid>
{I}                        <Grid x:Name="MediaPlayerPanel"
{I}                              RowSpacing="8"
{I}                              Visibility="Collapsed">
{I}                            <Grid.RowDefinitions>
{I}                                <RowDefinition Height="Auto" />
{I}                                <RowDefinition Height="*"    />
{I}                                <RowDefinition Height="Auto" />
{I}                            </Grid.RowDefinitions>

{I}                            <Grid Grid.Row="0" ColumnSpacing="11">
{I}                                <Grid.ColumnDefinitions>
{I}                                    <ColumnDefinition Width="Auto" />
{I}                                    <ColumnDefinition Width="*"    />
{I}                                </Grid.ColumnDefinitions>
{I}                                <Border Grid.Column="0"
{I}                                        Width="58" Height="58"
{I}                                        CornerRadius="9"
{I}                                        Background="#1AFFFFFF">
{I}                                    <Image x:Name="MediaArt" Stretch="UniformToFill" />
{I}                                </Border>
{I}                                <StackPanel Grid.Column="1" Spacing="2" VerticalAlignment="Center">
{I}                                    <TextBlock x:Name="MediaApp"
{I}                                               Text=""
{I}                                               FontSize="8.5"
{I}                                               FontWeight="SemiBold"
{I}                                               CharacterSpacing="90"
{I}                                               Foreground="#70FFFFFF"
{I}                                               TextTrimming="CharacterEllipsis" />
{I}                                    <TextBlock x:Name="MediaTitle"
{I}                                               Text="&#8212;"
{I}                                               FontFamily="Segoe UI Variable Display"
{I}                                               FontWeight="SemiBold"
{I}                                               FontSize="13"
{I}                                               Foreground="#F5FFFFFF"
{I}                                               TextWrapping="Wrap"
{I}                                               MaxLines="2"
{I}                                               TextTrimming="CharacterEllipsis" />
{I}                                    <TextBlock x:Name="MediaArtist"
{I}                                               Text=""
{I}                                               FontSize="11"
{I}                                               Foreground="#99FFFFFF"
{I}                                               TextTrimming="CharacterEllipsis" />
{I}                                </StackPanel>
{I}                            </Grid>

{I}                            <StackPanel Grid.Row="1" VerticalAlignment="Bottom" Spacing="4">
{I}                                <ProgressBar x:Name="MediaProgress"
{I}                                             Minimum="0" Maximum="1" Value="0"
{I}                                             Height="3"
{I}                                             CornerRadius="1.5"
{I}                                             Foreground="#D9A0E8"
{I}                                             Background="#26FFFFFF"
{I}                                             HorizontalAlignment="Stretch" />
{I}                                <Grid>
{I}                                    <Grid.ColumnDefinitions>
{I}                                        <ColumnDefinition Width="*"    />
{I}                                        <ColumnDefinition Width="Auto" />
{I}                                    </Grid.ColumnDefinitions>
{I}                                    <TextBlock x:Name="MediaElapsed"
{I}                                               Grid.Column="0"
{I}                                               Text="0:00"
{I}                                               FontSize="10"
{I}                                               Foreground="#77FFFFFF" />
{I}                                    <TextBlock x:Name="MediaRemaining"
{I}                                               Grid.Column="1"
{I}                                               Text="-0:00"
{I}                                               FontSize="10"
{I}                                               Foreground="#77FFFFFF" />
{I}                                </Grid>
{I}                            </StackPanel>

{I}                            <StackPanel Grid.Row="2"
{I}                                        Orientation="Horizontal"
{I}                                        HorizontalAlignment="Center"
{I}                                        Spacing="14">
{I}                                <Button x:Name="MediaPrev"
{I}                                        Click="MediaPrev_Click"
{I}                                        Width="30" Height="30"
{I}                                        Style="{StaticResource TabButtonStyle}"
{I}                                        ToolTipService.ToolTip="Previous">
{I}                                    <FontIcon Glyph="&#xE892;" FontSize="13" />
{I}                                </Button>
{I}                                <Button x:Name="MediaPlayPause"
{I}                                        Click="MediaPlayPause_Click"
{I}                                        Width="38" Height="38"
{I}                                        Padding="0"
{I}                                        CornerRadius="19"
{I}                                        Background="#F2FFFFFF"
{I}                                        BorderThickness="0"
{I}                                        ToolTipService.ToolTip="Play / Pause">
{I}                                    <FontIcon x:Name="MediaPlayPauseIcon"
{I}                                              Glyph="&#xE768;"
{I}                                              FontSize="16"
{I}                                              Foreground="#101010" />
{I}                                </Button>
{I}                                <Button x:Name="MediaNext"
{I}                                        Click="MediaNext_Click"
{I}                                        Width="30" Height="30"
{I}                                        Style="{StaticResource TabButtonStyle}"
{I}                                        ToolTipService.ToolTip="Next">
{I}                                    <FontIcon Glyph="&#xE893;" FontSize="13" />
{I}                                </Button>
{I}                            </StackPanel>
{I}                        </Grid>

{I}                        <Grid x:Name="RecentPanel" RowSpacing="9" Visibility="Visible">
{I}                            <Grid.RowDefinitions>
{I}                                <RowDefinition Height="Auto" />
{I}                                <RowDefinition Height="Auto" />
{I}                                <RowDefinition Height="Auto" />
{I}                                <RowDefinition Height="Auto" />
{I}                                <RowDefinition Height="*"    />
{I}                            </Grid.RowDefinitions>

{I}                            <Grid Grid.Row="0" Margin="1,0,0,2">
{I}                                <Grid.ColumnDefinitions>
{I}                                    <ColumnDefinition Width="*"    />
{I}                                    <ColumnDefinition Width="Auto" />
{I}                                </Grid.ColumnDefinitions>
{I}                                <TextBlock Grid.Column="0"
{I}                                           Text="RECENTLY COPIED"
{I}                                           FontSize="8.5"
{I}                                           FontWeight="SemiBold"
{I}                                           CharacterSpacing="90"
{I}                                           Foreground="#66FFFFFF"
{I}                                           VerticalAlignment="Center" />
{I}                                <Button Grid.Column="1"
{I}                                        Click="RecentChevron_Click"
{I}                                        Width="20" Height="20"
{I}                                        Padding="0"
{I}                                        Background="Transparent"
{I}                                        BorderThickness="0"
{I}                                        CornerRadius="5"
{I}                                        ToolTipService.ToolTip="Open clipboard history">
{I}                                    <FontIcon Glyph="&#xE76C;" FontSize="10" Foreground="#70FFFFFF" />
{I}                                </Button>
{I}                            </Grid>
""".replace("{I}", I)

block += recent(0, 1) + recent(1, 2) + recent(2, 3)

block += """
{I}                            <TextBlock x:Name="RecentEmptyText"
{I}                                       Grid.Row="4"
{I}                                       Text="Copy something and your latest items show up here; the player appears above once music is playing"
{I}                                       FontSize="10"
{I}                                       LineHeight="15"
{I}                                       Foreground="#5CFFFFFF"
{I}                                       TextWrapping="Wrap"
{I}                                       VerticalAlignment="Top"
{I}                                       Margin="1,4,0,0" />
{I}                        </Grid>
{I}                    </Grid>
{I}                </Border>

{I}                <!-- Lyrics card: shown beside the player while the track has lyrics -->
{I}                <Border x:Name="LyricsPanel"
{I}                        Grid.Column="1"
{I}                        CornerRadius="14"
{I}                        Background="#10FFFFFF"
{I}                        BorderBrush="#1AFFFFFF"
{I}                        BorderThickness="1"
{I}                        Padding="12,11,10,9"
{I}                        Visibility="Collapsed">
{I}                    <Grid>
{I}                        <Grid.RowDefinitions>
{I}                            <RowDefinition Height="Auto" />
{I}                            <RowDefinition Height="*"    />
{I}                            <RowDefinition Height="Auto" />
{I}                        </Grid.RowDefinitions>
{I}                        <TextBlock Grid.Row="0"
{I}                                   Text="LYRICS"
{I}                                   FontSize="8.5"
{I}                                   FontWeight="SemiBold"
{I}                                   CharacterSpacing="90"
{I}                                   Foreground="#66FFFFFF"
{I}                                   Margin="0,0,0,7" />
{I}                        <ScrollViewer Grid.Row="1"
{I}                                      VerticalScrollBarVisibility="Auto"
{I}                                      HorizontalScrollBarVisibility="Disabled">
{I}                            <StackPanel x:Name="LyricsStack" Spacing="4" />
{I}                        </ScrollViewer>
{I}                        <TextBlock Grid.Row="2"
{I}                                   Text="LRCLIB"
{I}                                   FontSize="8"
{I}                                   CharacterSpacing="60"
{I}                                   Foreground="#44FFFFFF"
{I}                                   HorizontalAlignment="Right"
{I}                                   Margin="0,6,0,0" />
{I}                    </Grid>
{I}                </Border>
{I}            </Grid>

{I}            <!-- Volume bar, under the player -->
{I}            <Border Grid.Row="2"
{I}                    CornerRadius="14"
{I}                    Background="#10FFFFFF"
{I}                    BorderBrush="#1AFFFFFF"
{I}                    BorderThickness="1"
{I}                    Padding="12,0,14,0">
{I}                <Grid>
{I}                    <Grid.ColumnDefinitions>
{I}                        <ColumnDefinition Width="Auto" />
{I}                        <ColumnDefinition Width="*"    />
{I}                        <ColumnDefinition Width="Auto" />
{I}                    </Grid.ColumnDefinitions>
{I}                    <FontIcon x:Name="VolumeIcon"
{I}                              Grid.Column="0"
{I}                              Glyph="&#xE993;"
{I}                              FontSize="13"
{I}                              Foreground="#90FFFFFF"
{I}                              VerticalAlignment="Center"
{I}                              Margin="0,0,10,0" />
{I}                    <Slider x:Name="VolumeSlider"
{I}                            Grid.Column="1"
{I}                            VerticalAlignment="Center"
{I}                            ValueChanged="VolumeSlider_ValueChanged" />
{I}                    <TextBlock x:Name="VolumeText"
{I}                               Grid.Column="2"
{I}                               Text="&#8212;"
{I}                               FontSize="10"
{I}                               Foreground="#90FFFFFF"
{I}                               VerticalAlignment="Center"
{I}                               TextAlignment="Right"
{I}                               Margin="10,0,0,0"
{I}                               MinWidth="30" />
{I}                </Grid>
{I}            </Border>
{I}        </Grid>

{I}        <!-- Tile grid: fills the right column top to bottom -->
{I}        <Grid x:Name="TileGrid"
{I}              Grid.Column="2"
{I}              RowSpacing="8">
{I}            <Grid.RowDefinitions>
{I}                <RowDefinition Height="*" />
{I}                <RowDefinition Height="*" />
{I}                <RowDefinition Height="*" />
{I}                <RowDefinition Height="*" />
{I}            </Grid.RowDefinitions>
{I}            <Grid.ColumnDefinitions>
{I}                <ColumnDefinition Width="*" />
{I}                <ColumnDefinition Width="*" />
{I}            </Grid.ColumnDefinitions>

{tiles}
{I}        </Grid>
{I}    </Grid>

{I}    <!-- Launcher: full width at the bottom -->
{I}    <Border Grid.Row="1"
{I}            CornerRadius="14"
{I}            Background="#10FFFFFF"
{I}            BorderBrush="#1AFFFFFF"
{I}            BorderThickness="1"
{I}            Padding="8,0,8,0"
{I}            AllowDrop="True"
{I}            DragOver="LauncherRow_DragOver"
{I}            Drop="LauncherRow_Drop">
{I}        <ScrollViewer HorizontalScrollBarVisibility="Auto"
{I}                      VerticalScrollBarVisibility="Disabled"
{I}                      HorizontalScrollMode="Enabled"
{I}                      VerticalScrollMode="Disabled">
{I}            <StackPanel x:Name="LauncherList"
{I}                        Orientation="Horizontal"
{I}                        Spacing="7"
{I}                        VerticalAlignment="Center">
{I}                <TextBlock x:Name="LauncherEmptyText"
{I}                           Text="Drop app shortcuts, folders or files here"
{I}                           FontSize="10"
{I}                           Foreground="#50FFFFFF"
{I}                           VerticalAlignment="Center"
{I}                           Margin="6,0,6,0" />
{I}                <Button x:Name="LauncherAddBtn"
{I}                        Width="30" Height="30"
{I}                        Padding="0"
{I}                        Background="Transparent"
{I}                        BorderBrush="#3DFFFFFF"
{I}                        BorderThickness="1"
{I}                        CornerRadius="9"
{I}                        Click="LauncherAddBtn_Click"
{I}                        ToolTipService.ToolTip="Add shortcut">
{I}                    <FontIcon Glyph="&#xE710;" FontSize="11" Foreground="#C0FFFFFF" />
{I}                </Button>
{I}            </StackPanel>
{I}        </ScrollViewer>
{I}    </Border>
""".replace("{tiles}", tiles).replace("{I}", I)

overlay_start = text.index('                        <!-- In-island Add Dialog Overlay -->', i)
overlay_end = text.index('                        </Grid>\n\n                    <!-- Clipboard tab -->', i)
overlay = text[overlay_start:overlay_end]

text = text[:i] + block + "\n" + overlay + text[j:]
open(path, "w", encoding="utf-8", newline="\n").write(text)
print("home rewritten:", len(block))
