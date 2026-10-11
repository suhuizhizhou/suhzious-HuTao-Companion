package local.hutao;

import java.nio.file.Files;
import java.nio.file.Path;
import java.util.concurrent.CountDownLatch;
import org.neo4j.harness.Neo4j;
import org.neo4j.harness.Neo4jBuilders;
import org.neo4j.configuration.GraphDatabaseSettings;
import org.neo4j.configuration.connectors.BoltConnector;
import org.neo4j.configuration.helpers.SocketAddress;

/** Real Community database for integration tests; random loopback port and owned data directory. */
public final class GraphFixture {
    public static void main(String[] args) throws Exception {
        if (args.length < 1 || args.length > 2) {
            throw new IllegalArgumentException("Expected owned output directory and optional fixed Bolt port");
        }
        Path root = Path.of(args[0]).toAbsolutePath();
        Files.createDirectories(root);
        var builder = Neo4jBuilders.newInProcessBuilder(root.resolve("database"))
            .withDisabledServer()
            .withConfig(GraphDatabaseSettings.pagecache_memory, 64L * 1024 * 1024);
        if (args.length == 2) {
            int port = Integer.parseInt(args[1]);
            if (port < 1024 || port > 65535) throw new IllegalArgumentException("Invalid Bolt port");
            builder = builder.withConfig(BoltConnector.listen_address, new SocketAddress("127.0.0.1", port));
        }
        Neo4j database = builder.build();
        Runtime.getRuntime().addShutdownHook(new Thread(database::close));
        Files.writeString(root.resolve("bolt-uri.txt"), database.boltURI().toString());
        System.out.println("Graph fixture ready (Neo4j Community 5.26.0)");
        new CountDownLatch(1).await();
    }
}
