# Quick Start

Get up and running with MeasFlow in minutes.

## Running the Quick Start Samples

Each language binding includes a runnable quickstart sample.

=== "C#"

    ```sh
    cd csharp/samples/QuickStart
    dotnet run
    ```

    The sample demonstrates:
    - Creating a measurement file
    - Adding groups and channels
    - Writing data with different types
    - Reading back the data
    - Accessing statistics

=== "Python"

    ```sh
    cd python
    pip install -e .
    python quickstart/quickstart.py
    ```

    The sample demonstrates:
    - Creating a measurement file with context manager
    - Adding groups and channels
    - Writing arrays of data
    - Reading data as NumPy arrays
    - Accessing statistics

=== "C"

    ```sh
    cd c

    # Build quickstart
    cmake -B build -DMEAS_BUILD_QUICKSTART=ON -DCMAKE_BUILD_TYPE=Release \
      -DMEAS_WITH_LZ4=OFF -DMEAS_WITH_ZSTD=OFF
    cmake --build build

    # Run
    ./build/quickstart
    ```

    The sample demonstrates:
    - Opening a writer
    - Creating groups and channels
    - Writing values
    - Closing the writer
    - Opening a reader
    - Reading values and statistics

## Your First Measurement File

Here's a minimal example to create and read a measurement file:

=== "C#"

    ```csharp
    using MeasFlow;

    // Write
    using (var writer = MeasFile.CreateWriter("demo.meas"))
    {
        var group = writer.AddGroup("Sensors");
        var temp = group.AddChannel<float>("Temperature");
        var pressure = group.AddChannel<double>("Pressure");

        temp.Write(25.5f);
        pressure.Write(101.3);

        writer.Flush();
    }

    // Read
    using (var reader = MeasFile.OpenRead("demo.meas"))
    {
        var temp = reader["Sensors"]["Temperature"];
        var data = temp.ReadAll<float>();
        var stats = temp.Statistics;

        Console.WriteLine($"Temperature: {data[0]}°C");
        Console.WriteLine($"Mean: {stats?.Mean}, StdDev: {stats?.StdDev}");
    }
    ```

=== "Python"

    ```python
    import measflow as meas

    # Write
    with meas.Writer("demo.meas") as writer:
        group = writer.add_group("Sensors")
        temp = group.add_channel("Temperature", meas.DataType.Float32)
        pressure = group.add_channel("Pressure", meas.DataType.Float64)

        temp.write([25.5])
        pressure.write([101.3])

        writer.flush()

    # Read
    with meas.Reader("demo.meas") as reader:
        temp = reader["Sensors"]["Temperature"]
        data = temp.read_all()
        stats = temp.statistics

        print(f"Temperature: {data[0]}°C")
        print(f"Mean: {stats.mean}, StdDev: {stats.std_dev}")
    ```

=== "C"

    ```c
    #include "measflow.h"
    #include <stdio.h>

    int main(void) {
        // Write
        meas_writer_t* writer = meas_writer_open("demo.meas");
        meas_group_t* group = meas_writer_add_group(writer, "Sensors");
        meas_channel_t* temp = meas_group_add_channel(group, "Temperature", MEAS_FLOAT32);
        meas_channel_t* pressure = meas_group_add_channel(group, "Pressure", MEAS_FLOAT64);

        float temp_val = 25.5f;
        double pressure_val = 101.3;

        meas_channel_write_f32(temp, &temp_val, 1);
        meas_channel_write_f64(pressure, &pressure_val, 1);

        meas_writer_flush(writer);
        meas_writer_close(writer);

        // Read
        meas_reader_t* reader = meas_reader_open("demo.meas");
        const meas_group_data_t* g = meas_reader_group_by_name(reader, "Sensors");
        const meas_channel_data_t* ch = meas_group_channel_by_name(g, "Temperature");

        float value;
        meas_channel_read_f32(ch, &value, 1);

        printf("Temperature: %.1f°C\n", value);

        if (ch->has_stats) {
            printf("Mean: %.1f, StdDev: %.1f\n", ch->stats.mean, ch->stats.std_dev);
        }

        meas_reader_close(reader);
        return 0;
    }
    ```

## Streaming Data

MeasFlow excels at streaming large amounts of data without running out of memory:

=== "C#"

    ```csharp
    using var writer = MeasFile.CreateWriter("recording.meas");
    var sensors = writer.AddGroup("Sensors");
    var temp = sensors.AddChannel<float>("Temperature");

    // Record continuously
    while (recording)
    {
        float[] batch = ReadFromSensor(batchSize: 10_000);
        temp.Write(batch.AsSpan());
        writer.Flush();  // Flushes to disk, clears buffer
    }
    // Memory usage stays constant regardless of recording duration!
    ```

=== "Python"

    ```python
    with meas.Writer("recording.meas") as writer:
        sensors = writer.add_group("Sensors")
        temp = sensors.add_channel("Temperature", meas.DataType.Float32)

        # Record continuously
        while recording:
            batch = read_from_sensor(batch_size=10_000)
            temp.write(batch)
            writer.flush()  # Flushes to disk, clears buffer
    # Memory usage stays constant!
    ```

=== "C"

    ```c
    meas_writer_t* w = meas_writer_open("recording.meas");
    meas_group_t* g = meas_writer_add_group(w, "Sensors");
    meas_channel_t* temp = meas_group_add_channel(g, "Temperature", MEAS_FLOAT32);

    // Record continuously
    while (recording) {
        float batch[10000];
        read_from_sensor(batch, 10000);
        meas_channel_write_f32(temp, batch, 10000);
        meas_writer_flush(w);  // Flushes to disk, clears buffer
    }
    meas_writer_close(w);
    // Memory usage stays constant!
    ```

## Next Steps

- [API Reference](../api/csharp.md) - Comprehensive API documentation
- [Streaming Architecture](../guide/streaming.md) - Learn about the streaming design
- [Bus Data Support](../guide/bus-data.md) - Working with CAN, LIN, FlexRay, etc.
- [Specification](../specification.md) - Complete binary format specification
